using RemoteOps.Contracts.Sync;
using RemoteOps.Sync;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Outbox em memória que imita o contrato de cursor do <c>LocalSyncClient</c>:
/// o id de cada mudança é a sua posição 1-based; <c>PullAsync(fromCursor)</c> devolve as
/// mudanças com id maior que o cursor e avança <see cref="CurrentCursor"/> para o máximo lido.
/// </summary>
internal sealed class FakeOutbox : ISyncClient
{
    private readonly List<SyncChange> _items = [];
    private long _cursor;

    public long CurrentCursor => _cursor;

    public Task PushAsync(IEnumerable<SyncChange> changes, CancellationToken ct = default)
    {
        _items.AddRange(changes);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SyncChange>> PullAsync(
        long fromCursor, int limit = 500, CancellationToken ct = default)
    {
        var page = new List<SyncChange>();
        long max = fromCursor;
        for (long id = fromCursor + 1; id <= _items.Count && page.Count < limit; id++)
        {
            page.Add(_items[(int)(id - 1)]);
            max = id;
        }

        _cursor = max;
        return Task.FromResult<IReadOnlyList<SyncChange>>(page);
    }
}
