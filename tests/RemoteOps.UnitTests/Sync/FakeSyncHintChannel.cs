using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>Canal de hints de teste: permite levantar <c>workspace.changed</c> manualmente.</summary>
internal sealed class FakeSyncHintChannel : ISyncHintChannel
{
    public event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    public bool Connected { get; private set; }

    public bool ThrowOnConnect { get; set; }

    public Task ConnectAsync(string workspaceId, CancellationToken ct = default)
    {
        if (ThrowOnConnect)
        {
            throw new InvalidOperationException("hub unreachable");
        }

        Connected = true;
        return Task.CompletedTask;
    }

    public async Task RaiseAsync(WorkspaceChangedHint hint)
    {
        if (WorkspaceChanged is not null)
        {
            await WorkspaceChanged(hint);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
