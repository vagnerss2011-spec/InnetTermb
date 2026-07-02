using System.Threading.Tasks;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class WorkspaceViewModelTests
{
    [Fact]
    public async Task Initialize_LoadsGroups()
    {
        var store = new InMemoryLocalStore();
        await store.AddGroupAsync("ws-local", "Innet");
        var hosts = new HostsViewModel(store, new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null), "ws-local");
        var browser = new BrowserViewModel(hosts, new KeychainViewModel(store, "ws-local"), new LogsViewModel());
        var vm = new WorkspaceViewModel(browser, new TabsViewModel());

        await vm.InitializeAsync();

        Assert.NotEmpty(vm.Browser.Hosts.Groups);
    }
}
