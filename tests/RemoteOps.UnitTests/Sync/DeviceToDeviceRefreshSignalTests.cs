using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Fase 2, com o caminho de dados REAL (applier de produção + SQLCipher, só a rede é fake): o sinal
/// <see cref="SyncOrchestrator.ChangesApplied"/> dispara quando o pull do device B materializa hosts
/// novos, e NÃO dispara num re-sync sem novidade. É o gatilho que faltava pra a lista sair do vazio
/// no 1º launch sem depender do relaunch.
/// </summary>
public sealed class DeviceToDeviceRefreshSignalTests
{
    private const string AccountPassword = "senha-da-conta-do-operador-2026";

    private static string NewServerWorkspace() => System.Guid.NewGuid().ToString();

    [Fact]
    public async Task FirstPull_WithNewHosts_FiresChangesApplied()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        // Device A cadastra um grupo + host + endpoint e sobe.
        await using (SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace))
        {
            AssetGroup group = await deviceA.Store.AddGroupAsync(SyncedDevice.VaultWorkspaceId, "Backbone");
            Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = SyncedDevice.VaultWorkspaceId,
                GroupId = group.Id,
                Name = "NE8000-CORE-01",
                DeviceRole = DeviceRoles.Router,
            });
            await deviceA.Store.AddEndpointAsync(new Endpoint
            {
                Id = System.Guid.NewGuid().ToString("n"),
                AssetId = asset.Id,
                Protocol = "ssh",
                Ipv4 = "10.20.30.40",
                Port = 22,
            });
            await deviceA.SyncOnceAsync();
        }

        // Device B nasce vazio e assina o sinal ANTES do primeiro sync.
        await using SyncedDevice deviceB =
            await SyncedDevice.CreateAsync("B", enrollment.Amk, changelog, secrets, serverWorkspace);
        int fired = 0;
        deviceB.Sync.ChangesApplied += () => fired++;

        await deviceB.SyncOnceAsync();

        // O host chegou ao banco E o sinal disparou (é ele que mandaria a UI recarregar).
        Assert.NotEmpty(await deviceB.Store.GetAssetsAsync(SyncedDevice.VaultWorkspaceId));
        Assert.True(fired >= 1, "ChangesApplied devia disparar quando o pull materializa hosts novos.");
    }

    [Fact]
    public async Task Resync_WithoutChanges_DoesNotFireChangesApplied()
    {
        var keys = new AccountKeyService();
        var changelog = new FakeChangelogApi();
        var secrets = new FakeSecretsApi();
        string serverWorkspace = NewServerWorkspace();
        AccountEnrollment enrollment = keys.Enroll(AccountPassword);

        await using (SyncedDevice deviceA =
            await SyncedDevice.CreateAsync("A", enrollment.Amk, changelog, secrets, serverWorkspace))
        {
            Asset asset = await deviceA.Store.AddAssetAsync(new AddAssetRequest
            {
                WorkspaceId = SyncedDevice.VaultWorkspaceId,
                Name = "SW-ACESSO-07",
                DeviceRole = DeviceRoles.Switch,
            });
            await deviceA.Store.AddEndpointAsync(new Endpoint
            {
                Id = System.Guid.NewGuid().ToString("n"),
                AssetId = asset.Id,
                Protocol = "ssh",
                Ipv4 = "192.168.1.7",
                Port = 22,
            });
            await deviceA.SyncOnceAsync();
        }

        await using SyncedDevice deviceB =
            await SyncedDevice.CreateAsync("B", enrollment.Amk, changelog, secrets, serverWorkspace);

        // 1º sync traz os dados (esperado disparar). Só DEPOIS assinamos, pra medir o 2º ciclo.
        await deviceB.SyncOnceAsync();

        int firedOnResync = 0;
        deviceB.Sync.ChangesApplied += () => firedOnResync++;

        // 2º e 3º ciclos: nada novo no servidor → applier não grava nada → sem aviso (nada de reload à toa).
        await deviceB.SyncOnceAsync();
        await deviceB.SyncOnceAsync();

        Assert.Equal(0, firedOnResync);
    }
}
