using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

internal sealed class FakeRemoteChangeApplier : IRemoteChangeApplier
{
    public List<SyncChange> Applied { get; } = [];

    public Task ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default)
    {
        Applied.AddRange(changes);
        return Task.CompletedTask;
    }
}
