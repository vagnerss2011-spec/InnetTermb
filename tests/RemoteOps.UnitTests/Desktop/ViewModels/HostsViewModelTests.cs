using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostsViewModelTests
{
    private static SessionLauncher Launcher(TabsViewModel tabs) => new(tabs, null, null, null, null, null, null);

    [Fact]
    public async Task LoadAsync_BuildsGroupCardsWithCounts()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r1" });
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r2" });
        var vm = new HostsViewModel(store, Launcher(new TabsViewModel()), "ws-local");

        await vm.LoadAsync();

        var card = vm.Groups.Single(c => c.Name == "Innet");
        Assert.Equal(2, card.HostCount);
        Assert.False(vm.IsInGroup);
    }

    [Fact]
    public async Task OpenGroup_LoadsHosts_AndBackReturns()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r1" });
        var vm = new HostsViewModel(store, Launcher(new TabsViewModel()), "ws-local");
        await vm.LoadAsync();

        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(20);
        Assert.True(vm.IsInGroup);
        Assert.Single(vm.Hosts);

        vm.BackCommand.Execute(null);
        Assert.False(vm.IsInGroup);
    }
}
