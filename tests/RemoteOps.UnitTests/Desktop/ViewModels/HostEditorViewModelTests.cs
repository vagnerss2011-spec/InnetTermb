using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorViewModelTests
{
    [Fact]
    public async Task Save_CreatesHostWithEndpoint()
    {
        var store = new InMemoryLocalStore();
        var g = await store.AddGroupAsync("ws-local", "Innet");
        var vm = new HostEditorViewModel(store, "ws-local", existing: null, groupId: g.Id)
        {
            Name = "r1",
            NewEndpointProtocol = "ssh",
            NewEndpointAddress = "10.0.0.1",
            NewEndpointPort = 22,
        };
        vm.AddEndpointCommand.Execute(null);

        await vm.SaveAsync();

        var assets = await store.GetAssetsAsync("ws-local", g.Id);
        var created = assets.Single(a => a.Name == "r1");
        Assert.Single(created.Endpoints);
        Assert.Equal("ssh", created.Endpoints[0].Protocol);
    }
}
