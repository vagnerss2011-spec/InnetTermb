using System.Threading;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Coordena o sync de um workspace atrás da feature flag <c>cloud.sync.enabled</c>: liga o canal de
/// hints (<see cref="ISyncHintChannel"/>) ao <see cref="SyncOrchestrator"/> (hint → pull incremental),
/// roda um laço de fundo por intervalo e — Fase 2, item A — dispara um sync incremental logo APÓS uma
/// edição local (push-ao-mudar), com debounce pra agrupar a rajada. Construído e iniciado pelo Desktop
/// apenas com a flag ON.
/// </summary>
public sealed class SyncSession : IAsyncDisposable
{
    private readonly SyncOrchestrator _orchestrator;
    private readonly ISyncHintChannel _hints;
    private readonly string _workspaceId;
    private readonly TimeSpan _interval;

    // ── Push-ao-mudar (Fase 2, item A) ──────────────────────────────────────────────────────
    // Uma edição local grava no outbox e levanta LocalChangePushed; em vez de sincronizar na hora
    // (rajada de edições = N ciclos), rearma um timer e só sincroniza quando os sinais PARAM por
    // _pushDebounce. Assim uma sequência de edições vira UM sync incremental. null = sem gatilho
    // (caminho legado/env-var sem a fonte de mudanças ligada).
    private readonly ISyncClient? _localChanges;
    private readonly TimeSpan _pushDebounce;
    private readonly Timer? _pushTimer;
    private readonly object _pushGate = new();

    private readonly TimeSpan _errorRetry;

    // ── Debounce do hint ────────────────────────────────────────────────────────────────────
    // Timer PRÓPRIO, e não o _pushTimer: aquele só existe quando há fonte local de mudanças, enquanto
    // o hint vem do servidor sempre. Janela curta (o hint é o caminho de tempo real), só o bastante
    // pra colapsar a rajada de um import.
    private readonly TimeSpan _hintDebounce;
    private readonly Timer _hintTimer;

    // ── Connect do canal de hints ───────────────────────────────────────────────────────────
    // Espera antes da PRIMEIRA nova tentativa e teto do backoff. Valores de código, não de tela: o
    // operador não tem como decidir isso melhor que o default, e o canal é best-effort de qualquer forma.
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ConnectRetryCeiling = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _connect;
    private bool _disposed;

    public SyncSession(
        SyncOrchestrator orchestrator,
        ISyncHintChannel hints,
        string workspaceId,
        TimeSpan interval,
        ISyncClient? localChanges = null,
        TimeSpan? pushDebounce = null,
        TimeSpan? errorRetry = null,
        TimeSpan? hintDebounce = null)
    {

        _orchestrator = orchestrator;
        _hints = hints;
        _workspaceId = workspaceId;
        _interval = interval;
        _localChanges = localChanges;
        _pushDebounce = pushDebounce ?? TimeSpan.FromSeconds(1.5);

        // Um ciclo que termina em Error esperava o intervalo INTEIRO antes de tentar de novo, então um
        // blip de rede custava minutos de atraso. Com o retry curto o erro transitório se resolve em
        // segundos, e o backoff evita martelar um servidor que está fora de verdade.
        _errorRetry = errorRetry ?? TimeSpan.FromSeconds(5);

        // 500ms: imperceptível pra quem espera a novidade aparecer na outra ponta, e ainda assim
        // suficiente pra engolir a rajada inteira de um import (o servidor emite um hint POR mudança).
        _hintDebounce = hintDebounce ?? TimeSpan.FromMilliseconds(500);
        _hintTimer = new Timer(_ => OnHintDebounceElapsed(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _hints.WorkspaceChanged += OnHintAsync;

        if (_localChanges is not null)
        {
            // Timer parado; cada LocalChangePushed o (re)arma pra disparar uma vez após a janela.
            _pushTimer = new Timer(_ => OnPushDebounceElapsed(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _localChanges.LocalChangePushed += OnLocalChangePushed;
        }
    }

    public SyncOrchestrator Orchestrator => _orchestrator;

    /// <summary>
    /// Canal de hints desta sessão, para quem precisa OBSERVAR o estado do tempo real (a barra de sync
    /// assina o <see cref="ISyncHintChannel.RealTimeChanged"/>). Quem monta o canal é a factory, então
    /// sem isto o Desktop teria que construir um segundo canal só para escutá-lo.
    /// </summary>
    public ISyncHintChannel Hints => _hints;

    /// <summary>
    /// Inicia o laço de fundo (sync imediato + por intervalo) e dispara o connect do canal de hints.
    /// Retorna assim que ambos estão EM CURSO — não espera o canal subir, porque ele é best-effort e
    /// pode demorar (ou nunca conseguir, em rede que bloqueia WebSocket).
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // O laço por intervalo começa primeiro: a sincronização não depende dos hints em tempo real,
        // que podem falhar em redes que bloqueiam WebSocket (ADR-010). Hints são best-effort.
        _loop = RunLoopAsync(_cts.Token);

        // Connect em FUNDO e com retry: a falha do primeiro connect era engolida aqui mesmo e o canal
        // nunca mais tentava — quem abre o app antes da VPN subir ficava em polling puro pelo resto da
        // sessão, sem nenhum sinal. Não se espera o connect porque o app não pode atrasar a abertura
        // por causa de um canal que é, por desenho, best-effort.
        _connect = ConnectHintsWithRetryAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Insiste no connect do canal de hints até conseguir (ou até o shutdown), com backoff que satura
    /// no teto. Só sai no sucesso: enquanto o canal está fora, a reconexão automática do SignalR nem
    /// entra em jogo — ela só cobre uma conexão que JÁ existiu.
    /// </summary>
    private async Task ConnectHintsWithRetryAsync(CancellationToken ct)
    {
        var delay = ConnectRetryDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hints.ConnectAsync(_workspaceId, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Sem hints em tempo real por ora; o laço por intervalo segue sincronizando. Não se
                // loga nada aqui: a mensagem carregaria a URL do hub, e o JWT viaja nela (ADR-013).
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            double next = Math.Min(delay.TotalMilliseconds * 2, ConnectRetryCeiling.TotalMilliseconds);
            delay = TimeSpan.FromMilliseconds(next);
        }
    }

    /// <summary>
    /// Flush final do outbox no fechamento (Fase 2, item A — o "Alt+F4"), LIMITADO por
    /// <paramref name="timeout"/>: drena só o push (<see cref="SyncOrchestrator.FlushOutboxAsync"/>) e
    /// nunca deixa o fechamento pendurado se a rede estiver fora. Best-effort — o que não subir sobe no
    /// próximo boot (outbox durável).
    ///
    /// <para><b>Ao BLOQUEAR nisto (GetResult) da UI thread, faça-o fora do contexto de
    /// sincronização</b> (ex.: <c>Task.Run</c>): o flush em si não toca o Dispatcher, mas os awaits do
    /// store SQLCipher capturam o SynchronizationContext atual, e continuações presas na UI thread
    /// bloqueada seriam deadlock. Ver <c>App.FlushOutboxOnClose</c>.</para>
    ///
    /// <para>A janela é limitada por dois mecanismos, de propósito: um CTS que cancela o flush (a rede
    /// honra o token) E um <see cref="Task.WhenAny(Task, Task)"/> contra um <see cref="Task.Delay(TimeSpan)"/>
    /// — assim, mesmo que uma chamada de rede ignore o cancelamento, o método RETORNA no timeout (o app
    /// segue encerrando; a task pendente morre com o processo).</para>
    /// </summary>
    public async Task FlushOutboxAsync(TimeSpan timeout)
    {
        var cts = new CancellationTokenSource();
        Task flush = _orchestrator.FlushOutboxAsync(cts.Token);
        Task completed = await Task.WhenAny(flush, Task.Delay(timeout));

        if (ReferenceEquals(completed, flush))
        {
            cts.Dispose(); // flush terminou dentro do teto — seguro descartar (nunca falha, ver orquestrador).
            return;
        }

        // Teto estourou: para de esperar (o app está fechando). Cancela o flush em voo pra ele soltar a
        // conexão, e agenda o descarte do CTS pra QUANDO ele terminar — descartar agora correria com o
        // token ainda em uso. O flush não falha (o orquestrador engole tudo), então nada a observar.
        cts.Cancel();
        _ = flush.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
    }

    private void OnLocalChangePushed()
    {
        lock (_pushGate)
        {
            if (_disposed)
            {
                return;
            }

            // (Re)arma o debounce: dispara uma vez após _pushDebounce sem novos sinais.
            _pushTimer?.Change(_pushDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnPushDebounceElapsed()
    {
        lock (_pushGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Fora do lock: o ciclo pode demorar (rede). Fire-and-forget guardado — um push-ao-mudar que
        // falha nunca pode virar exceção não observada nem travar nada; o próximo tick/sinal tenta de novo.
        _ = RunPushSyncAsync();
    }

    private async Task RunPushSyncAsync()
    {
        // Captura defensiva do CTS: um sinal em voo — edição local OU hint do servidor, ambos disparam
        // por aqui — pode chegar concorrente ao DisposeAsync, que descarta o _cts. _cts == null = ainda
        // não iniciado (sync com token None, best-effort); getter que lança ObjectDisposedException =
        // em shutdown → ignora.
        CancellationTokenSource? cts = _cts;
        CancellationToken token;
        if (cts is null)
        {
            token = CancellationToken.None;
        }
        else
        {
            try
            {
                token = cts.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        try
        {
            await _orchestrator.SyncOnceAsync(token);
        }
        catch (ObjectDisposedException)
        {
            // Sessão sendo descartada concorrentemente — best-effort, ignora.
        }
        catch (OperationCanceledException)
        {
            // Shutdown — esperado.
        }
        catch (Exception)
        {
            // SyncOnceAsync já não relança (fica em Error); guarda extra contra qualquer surpresa.
        }
    }

    private Task OnHintAsync(WorkspaceChangedHint hint)
    {
        // OrdinalIgnoreCase: o id trafega como string e já houve divergência de grafia entre o caminho
        // env-var (GUID cru, que o operador pode ter colado em maiúsculas) e o broadcast do servidor
        // (formato "D" minúsculo). Comparar diferenciando caixa descartava um hint LEGÍTIMO em silêncio.
        if (!string.Equals(hint.WorkspaceId, _workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // NÃO sincroniza aqui: o servidor emite UM hint POR mudança, então importar 200 hosts na outra
        // ponta enfileiraria ~200 ciclos completos no gate do orquestrador, cada um re-enumerando o
        // cofre. (Re)arma a janela; quando os hints PARAM, roda um ciclo só.
        lock (_pushGate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _hintTimer.Change(_hintDebounce, Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    private void OnHintDebounceElapsed()
    {
        lock (_pushGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Reaproveita o caminho guardado do push-ao-mudar: mesma captura defensiva do CTS e mesma
        // regra de engolir tudo — o disparo do hint é fire-and-forget e não pode virar exceção órfã.
        _ = RunPushSyncAsync();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int consecutiveErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            bool failed;
            try
            {
                await _orchestrator.SyncOnceAsync(ct);

                // SyncOnceAsync engole erro de rede e sai em Error em vez de relançar, então o estado
                // do orquestrador é a única leitura confiável de "o ciclo deu certo?".
                failed = _orchestrator.Status.State == SyncState.Error;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Este laço é a REDE DE SEGURANÇA do canal de hints — ele NÃO pode morrer. O erro de
                // rede já vem tratado (Error), mas um assinante de StatusChanged que lança (ex.:
                // Dispatcher.Invoke durante o shutdown) escapa por SetStatus e encerraria o polling em
                // silêncio: o device pararia de sincronizar sem nenhum sinal, até reiniciar o app. Ver
                // docs/superpowers/specs/2026-07-19-sync-tempo-real-resiliente-design.md.
                failed = true;
            }

            // Backoff só enquanto der erro; o primeiro sucesso volta ao intervalo normal.
            TimeSpan delay;
            if (failed)
            {
                consecutiveErrors++;
                double factor = Math.Pow(2, Math.Min(consecutiveErrors - 1, 3)); // 1x, 2x, 4x, 8x
                var backoff = TimeSpan.FromMilliseconds(_errorRetry.TotalMilliseconds * factor);
                delay = backoff < _interval ? backoff : _interval;
            }
            else
            {
                consecutiveErrors = 0;
                delay = _interval;
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Fecha os gatilhos debounced primeiro (push-ao-mudar e hint): marca o flag e para os timers,
        // para que nenhuma janela em voo dispare um SyncOnceAsync sobre um _cts em processo de descarte.
        lock (_pushGate)
        {
            _disposed = true;
        }

        if (_localChanges is not null)
        {
            _localChanges.LocalChangePushed -= OnLocalChangePushed;
        }

        _pushTimer?.Dispose();
        _hintTimer.Dispose();

        // Cancelar ANTES de fechar o canal: o retry do connect roda em fundo e insiste para sempre, e
        // sem o cancelamento ele chamaria ConnectAsync sobre um canal já descartado. Cancelar não é
        // descartar — o _cts só some no fim do método, depois que ninguém mais lê o token.
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_connect is not null)
        {
            try
            {
                await _connect;
            }
            catch (OperationCanceledException)
            {
                // esperado no shutdown
            }
        }

        // Desliga a fonte de hints (remove o handler e fecha a conexão SignalR) antes de descartar o
        // _cts, para que nenhum OnHintAsync novo dispare um ciclo sobre um CTS em processo de descarte.
        _hints.WorkspaceChanged -= OnHintAsync;
        await _hints.DisposeAsync();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // esperado no shutdown
            }
        }

        _cts?.Dispose();
    }
}
