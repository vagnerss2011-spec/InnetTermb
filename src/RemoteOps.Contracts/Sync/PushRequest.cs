namespace RemoteOps.Contracts.Sync;

/// <summary>
/// Corpo do <c>POST /sync/push</c>: lote de mudanças locais de um workspace a serem aplicadas no servidor.
/// </summary>
/// <param name="WorkspaceId">Workspace alvo (GUID em texto).</param>
/// <param name="Changes">Lote de mudanças do outbox local; não pode ser vazio.</param>
public sealed record PushRequest(
    string WorkspaceId,
    IReadOnlyList<SyncChange> Changes);
