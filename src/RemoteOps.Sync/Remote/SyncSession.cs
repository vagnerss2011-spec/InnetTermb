namespace RemoteOps.Sync.Remote;

/// <summary>
/// Coordena o sync de um workspace atrás da feature flag <c>cloud.sync.enabled</c>: liga o canal de
/// hints (<see cref="ISyncHintChannel"/>) ao <see cref="SyncOrchestrator"/> (hint → pull incremental)
/// e roda um laço de fundo por intervalo. Construído e iniciado pelo Desktop apenas com a flag ON.
/// </summary>
public sealed class SyncSession : IAsyncDisposable
{
    private readonly SyncOrchestrator _orchestrator;
    private readonly ISyncHintChannel _hints;
    private readonly string _workspaceId;
    private readonly TimeSpan _interval;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SyncSession(
        SyncOrchestrator orchestrator, ISyncHintChannel hints, string workspaceId, TimeSpan interval)
    {
        _orchestrator = orchestrator;
        _hints = hints;
        _workspaceId = workspaceId;
        _interval = interval;
        _hints.WorkspaceChanged += OnHintAsync;
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
