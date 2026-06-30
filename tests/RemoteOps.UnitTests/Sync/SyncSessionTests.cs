using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncSessionTests
{
    private static SyncOrchestrator Orchestrator(FakeCloudSyncApi api)
        => new("ws-1", new FakeOutbox(), api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

    [Fact]
    public async Task Hint_For_Workspace_Triggers_Sync()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await hints.RaiseAsync(new WorkspaceChangedHint("ws-1", 5, "asset", "e1"));

        Assert.NotEmpty(api.Pulls);
    }

    [Fact]
    public async Task Hint_For_Other_Workspace_Is_Ignored()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await hints.RaiseAsync(new WorkspaceChangedHint("ws-OTHER", 5, "asset", "e1"));

        Assert.Empty(api.Pulls);
    }

    [Fact]
    public async Task Start_Connects_The_Hint_Channel()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await session.StartAsync();

        Assert.True(hints.Connected);
    }

    [Fact]
    public async Task Start_Still_Syncs_When_Hint_Connect_Fails()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel { ThrowOnConnect = true };
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await session.StartAsync();

        // O laço por intervalo roda mesmo com o canal de hints indisponível (rede sem WebSocket).
        Assert.NotEmpty(api.Pulls);
    }
}
