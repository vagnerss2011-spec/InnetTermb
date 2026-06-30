using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

internal sealed class FakeSyncMetadataStore : ISyncMetadataStore
{
    private long _server;
    private long _outbox;

    public List<ConflictDetail> Conflicts { get; } = [];

    public long ServerCursor => _server;

    public long OutboxCursor => _outbox;

    public Task<SyncCursors> GetCursorsAsync(string workspaceId, CancellationToken ct = default)
        => Task.FromResult(new SyncCursors(_server, _outbox));

    public Task SaveServerCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        _server = cursor;
        return Task.CompletedTask;
    }

    public Task SaveOutboxCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        _outbox = cursor;
        return Task.CompletedTask;
    }

    public Task RecordConflictsAsync(IReadOnlyList<ConflictDetail> conflicts, CancellationToken ct = default)
    {
        Conflicts.AddRange(conflicts);
        return Task.CompletedTask;
    }

    public Task<int> GetConflictCountAsync(CancellationToken ct = default) => Task.FromResult(Conflicts.Count);
}
