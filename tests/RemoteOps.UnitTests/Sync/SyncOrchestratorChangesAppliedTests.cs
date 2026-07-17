using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Fase 2: o orquestrador precisa AVISAR a UI quando um pull grava algo local — e SÓ então. Estes
/// testes fixam o evento <see cref="SyncOrchestrator.ChangesApplied"/> nas fakes (a variante
/// end-to-end, com applier real e SQLCipher, está em <c>DeviceToDeviceRefreshSignalTests</c>).
/// </summary>
public sealed class SyncOrchestratorChangesAppliedTests
{
    private static SyncChange Change(string id, string op = "created")
        => new() { EntityType = "asset", EntityId = id, Operation = op, Patch = [] };

    [Fact]
    public async Task Fires_When_Pull_Applied_Changes()
    {
        var api = new FakeCloudSyncApi();
        api.PullResponses.Enqueue(new PullResponse([Change("e1"), Change("e2")], 3, false));
        var orch = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());
        int fired = 0;
        orch.ChangesApplied += () => fired++;

        await orch.SyncOnceAsync();

        Assert.Equal(1, fired); // um aviso por ciclo, não um por mudança
    }

    [Fact]
    public async Task Does_Not_Fire_When_Nothing_To_Pull()
    {
        // Tick de rotina sem nada novo no servidor: o applier nem é chamado → nada pra recarregar.
        var orch = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());
        int fired = 0;
        orch.ChangesApplied += () => fired++;

        await orch.SyncOnceAsync();

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task Does_Not_Fire_When_Applier_Reports_NoOp()
    {
        // O changelog trouxe mudanças, mas o applier as considerou no-op (versão antiga/idempotente):
        // applied == 0. Recarregar aqui seria reload à toa — o estado visível não mudou.
        var api = new FakeCloudSyncApi();
        api.PullResponses.Enqueue(new PullResponse([Change("e1", "updated")], 3, false));
        var applier = new FakeRemoteChangeApplier { ForcedAppliedCount = 0 };
        var orch = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), api, applier, new FakeSyncMetadataStore());
        int fired = 0;
        orch.ChangesApplied += () => fired++;

        await orch.SyncOnceAsync();

        Assert.NotEmpty(applier.Applied); // o applier FOI chamado
        Assert.Equal(0, fired);           // mas nada mudou de fato → sem aviso
    }

    [Fact]
    public async Task Does_Not_Fire_When_Cycle_Fails()
    {
        // Falha de rede no push: o ciclo termina em Error e nunca chega a aplicar changelog. Sem aviso.
        var outbox = new FakeOutbox();
        await outbox.PushAsync([Change("e1")]);
        var api = new FakeCloudSyncApi
        {
            PushHandler = _ => throw new CloudSyncException(System.Net.HttpStatusCode.InternalServerError),
        };
        var orch = new SyncOrchestrator(
            "ws-1", outbox, api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());
        int fired = 0;
        orch.ChangesApplied += () => fired++;

        await orch.SyncOnceAsync();

        Assert.Equal(SyncState.Error, orch.Status.State);
        Assert.Equal(0, fired);
    }
}
