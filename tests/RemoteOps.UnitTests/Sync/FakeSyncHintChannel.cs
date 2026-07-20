using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>Canal de hints de teste: permite levantar <c>workspace.changed</c> manualmente.</summary>
internal sealed class FakeSyncHintChannel : ISyncHintChannel
{
    public event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    public event Action<bool>? RealTimeChanged;

    public bool Connected { get; private set; }

    public bool ThrowOnConnect { get; set; }

    public bool IsRealTime { get; private set; }

    public Task ConnectAsync(string workspaceId, CancellationToken ct = default)
    {
        if (ThrowOnConnect)
        {
            throw new InvalidOperationException("hub unreachable");
        }

        Connected = true;
        SetRealTime(true);
        return Task.CompletedTask;
    }

    /// <summary>Simula a queda e a volta do canal, como fazem os handlers do SignalR.</summary>
    public void SetRealTime(bool value)
    {
        if (IsRealTime == value)
        {
            return;
        }

        IsRealTime = value;
        RealTimeChanged?.Invoke(value);
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
