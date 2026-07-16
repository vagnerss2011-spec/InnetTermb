using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public class HostEditorClassificationTests
{
    [Fact]
    public void SettingVendorModel_AutoSuggestsRole()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws", existing: null, groupId: null);
        vm.Vendor = "Huawei";
        vm.Model = "NE8000";
        Assert.Equal(DeviceRoles.Router, vm.DeviceRole);
    }

    [Fact]
    public void ManualOverride_StopsAutoSuggest()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws", existing: null, groupId: null);
        vm.Vendor = "Huawei";
        vm.Model = "NE8000";
        vm.DeviceRole = DeviceRoles.Switch; // override manual
        vm.Model = "NE9000";                 // muda o modelo → NÃO deve re-sugerir
        Assert.Equal(DeviceRoles.Switch, vm.DeviceRole);
    }

    [Fact]
    public void ExistingClassifiedHost_KeepsSavedRole()
    {
        var asset = new Asset
        {
            Id = "a1",
            WorkspaceId = "ws",
            Name = "r1",
            Vendor = "Huawei",
            Model = "S5720",
            DeviceRole = DeviceRoles.Switch,
        };
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws", existing: asset, groupId: null);
        Assert.Equal(DeviceRoles.Switch, vm.DeviceRole);
        Assert.Equal("Huawei", vm.Vendor);
    }

    [Fact]
    public async Task Save_PersistsVendorModelAndRole()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostEditorViewModel(store, "ws", existing: null, groupId: null)
        {
            Name = "rt1",
            Vendor = "MikroTik",
            Model = "CCR2004",
        };
        await vm.SaveAsync();

        var assets = await store.GetAssetsAsync("ws", null);
        var saved = Assert.Single(assets);
        Assert.Equal("MikroTik", saved.Vendor);
        Assert.Equal("CCR2004", saved.Model);
        Assert.Equal(DeviceRoles.Router, saved.DeviceRole);
    }
}
