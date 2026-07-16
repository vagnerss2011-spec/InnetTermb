using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Orquestra o ciclo de sync de um workspace (ADR-013), atrás da feature flag <c>cloud.sync.enabled</c>:
/// drena o outbox local → <c>POST /sync/push</c> → trata <see cref="PushResult"/> (avança o outbox cursor;
/// grava conflitos) → <c>GET /sync/pull</c> → aplica via <see cref="IRemoteChangeApplier"/> → avança o
/// server cursor → e, se houver, roda o canal dos <c>SecretEnvelope</c>
/// (<see cref="SecretSyncOrchestrator"/>). Expõe estado (Offline/Syncing/Synced/Error) + contagem de
/// conflitos. Nunca loga token, segredo ou patch — em falha apenas sinaliza <see cref="SyncState.Error"/>.
/// </summary>
public sealed class SyncOrchestrator
{
    private readonly string _workspaceId;
    private readonly ISyncClient _outbox;
    private readonly ICloudSyncApi _api;
    private readonly IRemoteChangeApplier _applier;
    private readonly ISyncMetadataStore _metadata;
    private readonly SecretSyncOrchestrator? _secrets;
    private readonly int _pageSize;

    /// <param name="secrets">
    /// Canal dos <c>SecretEnvelope</c> (spec §5), ou <c>null</c> pra sincronizar só metadados.
    /// Roda DENTRO deste ciclo, depois do changelog — ver <see cref="SecretSyncOrchestrator"/> pra
    /// por que separado em classe mas junto no ciclo.
    /// </param>
    public SyncOrchestrator(
        string workspaceId,
        ISyncClient outbox,
        ICloudSyncApi api,
        IRemoteChangeApplier applier,
        ISyncMetadataStore metadata,
        int pageSize = 200,
        SecretSyncOrchestrator? secrets = null)
    {
        _workspaceId = workspaceId;
        _outbox = outbox;
        _api = api;
        _applier = applier;
        _metadata = metadata;
        _pageSize = pageSize;
        _secrets = secrets;
    }

    public event Action<SyncStatus>? StatusChanged;

    public SyncStatus Status { get; private set; } = new(SyncState.Offline);

    // Serializa SyncOnceAsync: o laço por intervalo (SyncSession.RunLoopAsync) e o callback de hint
    // (SyncSession.OnHintAsync, disparado pelo SignalR) compartilham o MESMO outbox e os MESMOS
    // cursores. Sem exclusão mútua, dois ciclos concorrentes fazem read-modify-write não atômico do
    // outbox cursor / server cursor (o CurrentCursor do outbox é estado compartilhado) e podem PULAR
    // mudanças locais ou regredir o server cursor. Um ciclo por vez elimina a corrida (ADR-013).
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Executa um ciclo completo de push + pull. Em falha de rede/servidor fica em
    /// <see cref="SyncState.Error"/> (não relança) para que o laço de fundo siga no próximo intervalo.
    /// Serializado: chamadas concorrentes (hint + intervalo) rodam uma após a outra, nunca em paralelo.
    /// </summary>
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            SetStatus(SyncState.Syncing);
            try
            {
                SyncCursors cursors = await _metadata.GetCursorsAsync(_workspaceId, ct);
                await DrainOutboxAsync(cursors.OutboxCursor, ct);
                await ApplyServerChangesAsync(cursors.ServerCursor, ct);

                // Segredos DEPOIS dos metadados, sempre: assim o credential_ref já existe localmente
                // quando o envelope dele chega, e o device que recebe nunca fica com uma senha órfã.
                if (_secrets is not null)
                {
                    await _secrets.SyncOnceAsync(ct);
                }

                int conflicts = await _metadata.GetConflictCountAsync(ct);
                SetStatus(SyncState.Synced, conflicts);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Sem detalhes da exceção no estado/log — garante no-secret-in-log (ADR-013).
                SetStatus(SyncState.Error);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // PUSH: drena o outbox local em páginas e empurra para o servidor. A política de conflito
    // (OnConflictsAsync) decide se o cursor avança (skip) ou para (block). SecretEnvelope nunca
    // sofre auto-merge no cliente. Ver ADR-013.
    private async Task DrainOutboxAsync(long outboxCursor, CancellationToken ct)
    {
        while (true)
        {
            IReadOnlyList<SyncChange> batch = await _outbox.PullAsync(outboxCursor, _pageSize, ct);
            if (batch.Count == 0)
            {
                return;
            }

            PushResult result = await _api.PushAsync(new PushRequest(_workspaceId, batch), ct);
            if (result.Conflicts is { Count: > 0 } conflicts && !await OnConflictsAsync(conflicts, ct))
            {
                // Política decidiu interromper o push até a resolução do conflito.
                return;
            }

            outboxCursor = _outbox.CurrentCursor;
            await _metadata.SaveOutboxCursorAsync(_workspaceId, outboxCursor, ct);
        }
    }

    /// <summary>
    /// Política de conflito do cliente (ADR-013). Chamada quando o servidor rejeita ao menos uma
    /// mudança do lote — por versão obsoleta ou por <c>SecretEnvelope</c> (que NUNCA sofre auto-merge
    /// no cliente). Deve registrar os conflitos e decidir a posição do outbox:
    /// <list type="bullet">
    ///   <item><c>true</c>  → avança o cursor do outbox (segue ao próximo lote; usuário resolve depois).</item>
    ///   <item><c>false</c> → mantém o cursor (interrompe o push até a resolução).</item>
    /// </list>
    /// </summary>
    private async Task<bool> OnConflictsAsync(IReadOnlyList<ConflictDetail> conflicts, CancellationToken ct)
    {
        // Política: record & advance (ADR-013). Registra o conflito e avança o cursor do outbox —
        // não trava o sync; o usuário resolve depois. SecretEnvelope é apenas registrado, NUNCA
        // mesclado nem auto-resolvido aqui (CLAUDE.md/ADR-003): o cliente nunca decide segredo.
        await _metadata.RecordConflictsAsync(conflicts, ct);
        return true;
    }

    // PULL: puxa o changelog do servidor em páginas e aplica localmente (idempotente, sem re-emitir
    // no outbox). O server cursor avança a cada página confirmada.
    private async Task ApplyServerChangesAsync(long serverCursor, CancellationToken ct)
    {
        while (true)
        {
            PullResponse response = await _api.PullAsync(_workspaceId, serverCursor, _pageSize, ct);
            if (response.Changes.Count > 0)
            {
                await _applier.ApplyAsync(response.Changes, ct);
            }

            serverCursor = response.NextCursor;
            await _metadata.SaveServerCursorAsync(_workspaceId, serverCursor, ct);

            if (!response.HasMore)
            {
                return;
            }
        }
    }

    private void SetStatus(SyncState state, int conflictCount = 0)
    {
        Status = new SyncStatus(state, conflictCount);
        StatusChanged?.Invoke(Status);
    }
}
