using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncOrchestratorTests
{
    private static SyncChange Change(string id, string op = "created")
        => new() { EntityType = "asset", EntityId = id, Operation = op, Patch = [] };

    [Fact]
    public async Task SyncOnce_Pushes_Outbox_Then_Pulls_And_Applies()
    {
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("e1"), Change("e2", "updated")]);
        var api = new FakeCloudSyncApi();
        api.PushResults.Enqueue(new PushResult("ok", 5));
        api.PullResponses.Enqueue(new PullResponse([Change("e9", "updated")], 12, false));
        var applier = new FakeRemoteChangeApplier();
        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator("ws-1", outbox, api, applier, metadata);

        await orch.SyncOnceAsync();

        PushRequest push = Assert.Single(api.Pushes);
        Assert.Equal("ws-1", push.WorkspaceId);
        Assert.Equal(2, push.Changes.Count);
        Assert.Equal(2, metadata.OutboxCursor);
        Assert.Equal("ws-1", api.Pulls[0].Workspace);
        Assert.Equal(0, api.Pulls[0].Cursor);
        SyncChange applied = Assert.Single(applier.Applied);
        Assert.Equal("e9", applied.EntityId);
        Assert.Equal(12, metadata.ServerCursor);
        Assert.Equal(SyncState.Synced, orch.Status.State);
        Assert.Equal(0, orch.Status.ConflictCount);
    }

    [Fact]
    public async Task SyncOnce_Raises_Syncing_Then_Synced()
    {
        var orch = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());
        var states = new List<SyncState>();
        orch.StatusChanged += s => states.Add(s.State);

        await orch.SyncOnceAsync();

        Assert.Equal([SyncState.Syncing, SyncState.Synced], states);
    }

    [Fact]
    public async Task SyncOnce_Sets_Error_On_Failure()
    {
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("e1")]);
        var api = new FakeCloudSyncApi
        {
            PushHandler = _ => throw new CloudSyncException(System.Net.HttpStatusCode.InternalServerError),
        };
        var orch = new SyncOrchestrator(
            "ws-1", outbox, api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        await orch.SyncOnceAsync();

        Assert.Equal(SyncState.Error, orch.Status.State);
    }

    [Fact]
    public async Task SyncOnce_Pulls_All_Pages()
    {
        var api = new FakeCloudSyncApi();
        api.PullResponses.Enqueue(new PullResponse([Change("e1")], 5, true));
        api.PullResponses.Enqueue(new PullResponse([Change("e2")], 9, false));
        var applier = new FakeRemoteChangeApplier();
        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator("ws-1", new FakeOutbox(), api, applier, metadata);

        await orch.SyncOnceAsync();

        Assert.Equal(2, api.Pulls.Count);
        Assert.Equal(0, api.Pulls[0].Cursor);
        Assert.Equal(5, api.Pulls[1].Cursor);
        Assert.Equal(2, applier.Applied.Count);
        Assert.Equal(9, metadata.ServerCursor);
    }

    [Fact]
    public async Task SyncOnce_Does_Not_Push_When_Outbox_Empty()
    {
        var api = new FakeCloudSyncApi();
        var orch = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        await orch.SyncOnceAsync();

        Assert.Empty(api.Pushes);
    }

    [Fact]
    public async Task SyncOnce_Records_Conflicts_And_Advances_Outbox()
    {
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("s1", "updated")]);
        var api = new FakeCloudSyncApi();
        api.PushResults.Enqueue(new PushResult("conflict", 0,
        [
            new ConflictDetail("c1", "SecretEnvelope", "s1", 1, -1, "secret-envelope.no-auto-merge"),
        ]));
        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator("ws-1", outbox, api, new FakeRemoteChangeApplier(), metadata);

        await orch.SyncOnceAsync();

        ConflictDetail recorded = Assert.Single(metadata.Conflicts);
        Assert.Equal("secret-envelope.no-auto-merge", recorded.Reason);
        Assert.Equal(1, metadata.OutboxCursor);
        Assert.Equal(SyncState.Synced, orch.Status.State);
        Assert.Equal(1, orch.Status.ConflictCount);
    }
}
