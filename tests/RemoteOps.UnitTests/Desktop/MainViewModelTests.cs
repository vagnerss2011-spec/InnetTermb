using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsGroupsAndHosts()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "DC1");
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", Name = "router-01" });

        var vm = new MainViewModel(store);
        await vm.InitializeAsync();

        Assert.Single(vm.Sidebar.Groups);
        Assert.Single(vm.HostList.Assets);
    }

    [Fact]
    public async Task SelectGroup_FiltersHostList()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Core");
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "sw-core-01" });
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", Name = "dmz-fw-01" });

        var vm = new MainViewModel(store);
        await vm.InitializeAsync();
        Assert.Equal(2, vm.HostList.Assets.Count);

        vm.Sidebar.SelectedGroup = vm.Sidebar.Groups[0];
        // O evento GroupSelected dispara LoadAsync — aguardar com LoadAsync explícito
        await vm.HostList.LoadAsync(vm.Sidebar.Groups[0].Id);

        Assert.Single(vm.HostList.Assets);
        Assert.Equal("sw-core-01", vm.HostList.Assets[0].Name);
    }

    [Fact]
    public async Task SelectHost_PopulatesInspector()
    {
        var store = new InMemoryLocalStore();
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", Name = "ntp-01" });

        var vm = new MainViewModel(store);
        await vm.InitializeAsync();
        vm.HostList.SelectedAsset = vm.HostList.Assets[0];

        Assert.True(vm.Inspector.HasAsset);
        Assert.Equal("ntp-01", vm.Inspector.Asset!.Name);
    }

    [Fact]
    public async Task OpenSession_ViaInspector_CreatesTab()
    {
        var store = new InMemoryLocalStore();
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", Name = "router-core" });

        var vm = new MainViewModel(store);
        await vm.InitializeAsync();
        vm.HostList.SelectedAsset = vm.HostList.Assets[0];

        vm.Inspector.OpenSessionCommand.Execute(RemoteProtocol.Ssh);

        Assert.Single(vm.Tabs.Tabs);
        Assert.Equal(RemoteProtocol.Ssh, vm.Tabs.Tabs[0].Protocol);
    }

    [Fact]
    public async Task SearchText_FiltersHostList()
    {
        var store = new InMemoryLocalStore();
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", Name = "router-alfa" });
        await store.AddAssetAsync(new Domain.AddAssetRequest { WorkspaceId = "ws-local", Name = "switch-beta" });

        var vm = new MainViewModel(store);
        await vm.InitializeAsync();
        Assert.Equal(2, vm.HostList.Assets.Count);

        vm.SearchText = "router";
        await vm.HostList.LoadAsync(); // fire-and-forget em SearchText.set; chamar diretamente

        Assert.Single(vm.HostList.Assets);
    }
}
