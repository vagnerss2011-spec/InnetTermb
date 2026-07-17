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

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public SyncSession(
        SyncOrchestrator orchestrator,
        ISyncHintChannel hints,
        string workspaceId,
        TimeSpan interval,
        ISyncClient? localChanges = null,
        TimeSpan? pushDebounce = null)
    {
        _orchestrator = orchestrator;
        _hints = hints;
        _workspaceId = workspaceId;
        _interval = interval;
        _localChanges = localChanges;
        _pushDebounce = pushDebounce ?? TimeSpan.FromSeconds(1.5);
        _hints.WorkspaceChanged += OnHintAsync;

        if (_localChanges is not null)
        {
            // Timer parado; cada LocalChangePushed o (re)arma pra disparar uma vez após a janela.
            _pushTimer = new Timer(_ => OnPushDebounceElapsed(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _localChanges.LocalChangePushed += OnLocalChangePushed;
        }
    }

    public SyncOrchestrator Orchestrator => _orchestrator;

    /// <summary>Conecta o canal de hints e inicia o laço de fundo (sync imediato + por intervalo).</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // O laço por intervalo começa primeiro: a sincronização não depende dos hints em tempo real,
        // que podem falhar em redes que bloqueiam WebSocket (ADR-010). Hints são best-effort.
        _loop = RunLoopAsync(_cts.Token);
        try
        {
            await _hints.ConnectAsync(_workspaceId, _cts.Token);
        }
        catch (Exception)
        {
            // Sem hints em tempo real; o laço por intervalo ainda sincroniza.
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
        // Mesma captura defensiva do CTS que OnHintAsync: um sinal em voo pode chegar concorrente ao
        // DisposeAsync, que descarta o _cts. _cts == null = ainda não iniciado (sync com token None,
        // best-effort); getter que lança ObjectDisposedException = em shutdown → ignora.
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

    private async Task OnHintAsync(WorkspaceChangedHint hint)
    {
        if (!string.Equals(hint.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return;
        }

        // Captura o CTS num local: um hint em voo pode chegar concorrente ao DisposeAsync, que
        // descarta o _cts. Ler _cts.Token depois do Dispose lançaria ObjectDisposedException num Task
        // órfão (o SignalR invoca este handler fora do nosso controle). _cts == null significa "ainda
        // não iniciado" (sync best-effort com token None, comportamento preservado); já um getter Token
        // que lança ObjectDisposedException significa "em shutdown" → ignora.
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
                return; // sessão descartada concorrentemente — hint best-effort, ignora.
            }
        }

        try
        {
            await _orchestrator.SyncOnceAsync(token);
        }
        catch (ObjectDisposedException)
        {
            // Sessão sendo descartada concorrentemente — hint é best-effort, ignora.
        }
        catch (OperationCanceledException)
        {
            // Shutdown — esperado.
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _orchestrator.SyncOnceAsync(ct);
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Fecha a fonte de push-ao-mudar primeiro: remove o handler e para o timer para que nenhum
        // OnPushDebounceElapsed novo dispare um SyncOnceAsync sobre um _cts em processo de descarte.
        lock (_pushGate)
        {
            _disposed = true;
        }

        if (_localChanges is not null)
        {
            _localChanges.LocalChangePushed -= OnLocalChangePushed;
        }

        _pushTimer?.Dispose();

        // Ordem importa: primeiro desliga a fonte de hints (remove o handler e fecha a conexão
        // SignalR) para que nenhum OnHintAsync novo seja disparado; só então cancela e descarta o
        // _cts. Isso fecha a janela em que um hint em voo leria um CTS já descartado.
        _hints.WorkspaceChanged -= OnHintAsync;
        await _hints.DisposeAsync();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

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
