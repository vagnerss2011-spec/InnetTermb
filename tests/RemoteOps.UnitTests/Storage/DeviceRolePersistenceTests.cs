using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Storage;

public class DeviceRolePersistenceTests
{
    [Fact]
    public async Task InMemory_RoundTrips_DeviceRole()
    {
        var store = new InMemoryLocalStore();
        Asset added = await store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = "ws",
            Name = "rt1",
            Vendor = "Huawei",
            Model = "NE8000",
            DeviceRole = DeviceRoles.Router,
        });
        Assert.Equal(DeviceRoles.Router, added.DeviceRole);

        Asset? fetched = await store.GetAssetAsync(added.Id);
        Assert.Equal(DeviceRoles.Router, fetched!.DeviceRole);
    }

    [Fact]
    public async Task SqlCipher_RoundTrips_And_Persists_Across_Restart()
    {
        using var tc = await StoreTestContext.CreateAsync();

        Asset added = await tc.Store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = tc.WorkspaceId,
            Name = "rt1",
            Vendor = "Huawei",
            Model = "NE8000",
            DeviceRole = DeviceRoles.Router,
        });
        Assert.Equal(DeviceRoles.Router, added.DeviceRole);

        Asset? fetched = await tc.Store.GetAssetAsync(added.Id);
        Assert.Equal(DeviceRoles.Router, fetched!.DeviceRole);

        // Reclassifica (operador troca o papel) → persiste.
        await tc.Store.UpdateAssetAsync(new Asset
        {
            Id = fetched.Id,
            WorkspaceId = fetched.WorkspaceId,
            GroupId = fetched.GroupId,
            Name = fetched.Name,
            Vendor = fetched.Vendor,
            Model = fetched.Model,
            DeviceRole = DeviceRoles.Switch,
            Site = fetched.Site,
            Tags = fetched.Tags,
            Version = fetched.Version,
        });

        // "Reinicia" o app (novo store sobre o mesmo banco cifrado).
        SqlCipherLocalStore reopened = await tc.ReopenStoreAsync();
        Asset? after = await reopened.GetAssetAsync(added.Id);
        Assert.Equal(DeviceRoles.Switch, after!.DeviceRole);
    }

    [Fact]
    public async Task SqlCipher_NullDeviceRole_StaysNull()
    {
        using var tc = await StoreTestContext.CreateAsync();
        Asset added = await tc.Store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = tc.WorkspaceId,
            Name = "sem-tipo",
        });
        Asset? fetched = await tc.Store.GetAssetAsync(added.Id);
        Assert.Null(fetched!.DeviceRole);
    }
}
