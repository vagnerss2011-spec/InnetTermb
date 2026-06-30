using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>Cursores de sync persistidos: do servidor (changelog) e do outbox enviado.</summary>
public readonly record struct SyncCursors(long ServerCursor, long OutboxCursor);

/// <summary>
/// Persiste os cursores de sync e os conflitos (<see cref="ConflictDetail"/>) — na prática, no
/// mesmo banco SQLCipher do workspace (ADR-013). O conflito nunca contém segredo nem patch sensível.
/// </summary>
public interface ISyncMetadataStore
{
    Task<SyncCursors> GetCursorsAsync(string workspaceId, CancellationToken ct = default);

    Task SaveServerCursorAsync(string workspaceId, long cursor, CancellationToken ct = default);

    Task SaveOutboxCursorAsync(string workspaceId, long cursor, CancellationToken ct = default);

    Task RecordConflictsAsync(IReadOnlyList<ConflictDetail> conflicts, CancellationToken ct = default);

    Task<int> GetConflictCountAsync(CancellationToken ct = default);
}
