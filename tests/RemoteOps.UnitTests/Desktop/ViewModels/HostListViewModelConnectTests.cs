using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostListViewModelConnectTests
{
    [Fact]
    public void ConnectCommand_DisabledWithoutSelection()
    {
        var vm = new HostListViewModel(new InMemoryLocalStore(), "ws-local");
        Assert.False(vm.ConnectCommand.CanExecute("ssh"));
    }

    [Fact]
    public async Task ConnectCommand_RaisesConnectRequestedWithProtocol()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostListViewModel(store, "ws-local");
        var asset = await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", Name = "r1" });
        vm.SelectedAsset = new AssetViewModel(asset);

        string? received = null;
        vm.ConnectRequested += (_, proto) => received = proto;
        vm.ConnectCommand.Execute("telnet");

        Assert.Equal("telnet", received);
    }
}
