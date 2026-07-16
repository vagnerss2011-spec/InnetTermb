using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostsFilterTests
{
    private static SessionLauncher Launcher() => new(new TabsViewModel(), null, null, null, null, null, null);

    private static async Task<HostsViewModel> OpenGroupWithHostsAsync()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws", "Cliente A");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws", GroupId = g.Id, Name = "rt-1", Vendor = "Huawei", Model = "NE8000", DeviceRole = DeviceRoles.Router });
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws", GroupId = g.Id, Name = "sw-1", Vendor = "Huawei", Model = "S5720", DeviceRole = DeviceRoles.Switch });
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws", GroupId = g.Id, Name = "srv-1", Vendor = "Debian", DeviceRole = DeviceRoles.ServerLinux });

        var vm = new HostsViewModel(store, Launcher(), "ws");
        await vm.LoadAsync();
        vm.OpenGroupCommand.Execute(vm.Groups.Single());
        await Task.Delay(40);
        return vm;
    }

    [Fact]
    public async Task Chips_ListOnlyPresentRolesAndVendors()
    {
        var vm = await OpenGroupWithHostsAsync();

        var keys = vm.DeviceFilters.Select(f => f.Key).ToList();
        Assert.Equal(string.Empty, keys[0]); // "Todos" primeiro
        Assert.Contains("role:router", keys);
        Assert.Contains("role:switch", keys);
        Assert.Contains("role:server-linux", keys);
        Assert.Contains("vendor:huawei", keys);
        Assert.Contains("vendor:debian", keys);
        Assert.DoesNotContain("role:olt", keys); // não presente → sem chip
    }

    [Fact]
    public async Task ApplyRoleFilter_ShowsOnlyThatRole()
    {
        var vm = await OpenGroupWithHostsAsync();
        Assert.Equal(3, vm.Hosts.Count); // "Todos" por padrão

        vm.ApplyFilterCommand.Execute(vm.DeviceFilters.Single(f => f.Key == "role:router"));
        await Task.Delay(40);

        Assert.Single(vm.Hosts);
        Assert.Equal("rt-1", vm.Hosts[0].Name);
        Assert.True(vm.DeviceFilters.Single(f => f.Key == "role:router").IsActive);
    }

    [Fact]
    public async Task ApplyVendorFilter_ShowsOnlyThatVendor()
    {
        var vm = await OpenGroupWithHostsAsync();

        vm.ApplyFilterCommand.Execute(vm.DeviceFilters.Single(f => f.Key == "vendor:huawei"));
        await Task.Delay(40);

        Assert.Equal(2, vm.Hosts.Count); // NE8000 + S5720
        Assert.All(vm.Hosts, h => Assert.Equal("huawei", h.VendorKey));
    }

    [Fact]
    public async Task ApplyTodos_ShowsAllAgain()
    {
        var vm = await OpenGroupWithHostsAsync();

        vm.ApplyFilterCommand.Execute(vm.DeviceFilters.Single(f => f.Key == "role:switch"));
        await Task.Delay(40);
        Assert.Single(vm.Hosts);

        vm.ApplyFilterCommand.Execute(vm.DeviceFilters.Single(f => f.Key == string.Empty));
        await Task.Delay(40);
        Assert.Equal(3, vm.Hosts.Count);
    }
}
