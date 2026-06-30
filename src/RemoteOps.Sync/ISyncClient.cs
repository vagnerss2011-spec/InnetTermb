using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync;

// TODO: Implementar na frente feature/sync-local.
// Outbox local em SQLite + push/pull com cursor para o Cloud API.
public interface ISyncClient
{
    long CurrentCursor { get; }

    Task PushAsync(IEnumerable<SyncChange> changes, CancellationToken ct = default);

    Task<IReadOnlyList<SyncChange>> PullAsync(long fromCursor, int limit = 500, CancellationToken ct = default);
}
