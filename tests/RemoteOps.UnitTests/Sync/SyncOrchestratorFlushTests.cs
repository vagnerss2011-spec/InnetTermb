using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Fase 2, item A ("flush-ao-fechar"): <see cref="SyncOrchestrator.FlushOutboxAsync"/> drena SÓ o push,
/// sem pull, e nunca relança em falha — fechar o app não pode travar por causa do sync.
/// </summary>
public sealed class SyncOrchestratorFlushTests
{
    private static SyncChange Change(string id)
        => new() { EntityType = "asset", EntityId = id, Operation = "created", Patch = [] };

    [Fact]
    public async Task Flush_Drains_The_Outbox_Push_Without_Pulling()
    {
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("e1"), Change("e2")]);
        var api = new FakeCloudSyncApi();
        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator("ws-1", outbox, api, new FakeRemoteChangeApplier(), metadata);

        await orch.FlushOutboxAsync();

        Assert.NotEmpty(api.Pushes);       // subiu o pendente
        Assert.Empty(api.Pulls);           // flush NÃO puxa
        Assert.Equal(2, metadata.OutboxCursor); // cursor do outbox avançou
    }

    [Fact]
    public async Task Flush_Does_Not_Change_Status_To_Avoid_Dispatcher_On_Close()
    {
        // O flush roda no fechamento, onde a UI thread costuma estar bloqueada esperando por ele:
        // levantar StatusChanged daí (que marshala pro Dispatcher) seria deadlock. Então o estado NÃO
        // muda por causa do flush.
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("e1")]);
        var orch = new SyncOrchestrator(
            "ws-1", outbox, new FakeCloudSyncApi(), new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());
        int statusEvents = 0;
        orch.StatusChanged += _ => statusEvents++;

        await orch.FlushOutboxAsync();

        Assert.Equal(0, statusEvents);
        Assert.Equal(SyncState.Offline, orch.Status.State); // segue no estado inicial
    }

    [Fact]
    public async Task Flush_Offline_Does_Not_Throw()
    {
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("e1")]);
        var api = new FakeCloudSyncApi
        {
            PushHandler = _ => throw new CloudSyncException(System.Net.HttpStatusCode.ServiceUnavailable),
        };
        var orch = new SyncOrchestrator("ws-1", outbox, api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        // Não relança: rede fora no fechamento é rotina, não erro.
        await orch.FlushOutboxAsync();
    }

    [Fact]
    public async Task Flush_On_Empty_Outbox_Is_A_Noop()
    {
        var api = new FakeCloudSyncApi();
        var orch = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        await orch.FlushOutboxAsync();

        Assert.Empty(api.Pushes);
        Assert.Empty(api.Pulls);
    }
}
