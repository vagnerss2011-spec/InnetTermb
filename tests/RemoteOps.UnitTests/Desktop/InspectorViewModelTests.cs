using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class InspectorViewModelTests
{
    private static async Task<(InspectorViewModel Vm, AssetViewModel Asset)> BuildWithAsset()
    {
        var store = new InMemoryLocalStore();
        var asset = await store.AddAssetAsync(new AddAssetRequest
        {
            WorkspaceId = "ws-test",
            Name = "router-01",
        });
        var vm = new InspectorViewModel(store);
        var assetVm = new AssetViewModel(asset);
        vm.Asset = assetVm;
        return (vm, assetVm);
    }

    [Fact]
    public async Task HasAsset_TrueWhenAssetSet()
    {
        var (vm, _) = await BuildWithAsset();
        Assert.True(vm.HasAsset);
    }

    [Fact]
    public void HasAsset_FalseWhenNoAsset()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore());
        Assert.False(vm.HasAsset);
    }

    [Fact]
    public async Task AddEndpoint_StoresEndpoint()
    {
        var (vm, _) = await BuildWithAsset();
        vm.NewEndpointProtocol = RemoteProtocol.Ssh;
        vm.NewEndpointAddress = "192.168.1.1";
        vm.NewEndpointPort = 22;

        await vm.AddEndpointAsync();

        Assert.Equal(string.Empty, vm.NewEndpointAddress);
    }

    [Fact]
    public async Task AddEndpoint_EmptyAddress_DoesNotExecute()
    {
        var (vm, _) = await BuildWithAsset();
        vm.NewEndpointAddress = string.Empty;

        Assert.False(vm.AddEndpointCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddEndpoint_WithoutAsset_DoesNotExecute()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore());
        vm.NewEndpointAddress = "10.0.0.1";

        Assert.False(vm.AddEndpointCommand.CanExecute(null));
    }

    [Fact]
    public async Task DefaultPort_ChangesWithProtocol()
    {
        var (vm, _) = await BuildWithAsset();

        vm.NewEndpointProtocol = RemoteProtocol.Rdp;
        Assert.Equal(3389, vm.NewEndpointPort);

        vm.NewEndpointProtocol = RemoteProtocol.Telnet;
        Assert.Equal(23, vm.NewEndpointPort);

        vm.NewEndpointProtocol = RemoteProtocol.Ssh;
        Assert.Equal(22, vm.NewEndpointPort);
    }

    [Fact]
    public async Task OpenSession_FiresSessionRequested()
    {
        var (vm, asset) = await BuildWithAsset();
        OpenSessionRequest? req = null;
        vm.SessionRequested += (_, r) => req = r;

        vm.OpenSessionCommand.Execute(RemoteProtocol.Ssh);

        Assert.NotNull(req);
        Assert.Equal(RemoteProtocol.Ssh, req!.Protocol);
        Assert.Equal(asset.Name, req.AssetName);
    }

    [Fact]
    public void OpenSession_WithoutAsset_DoesNotFire()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore());
        bool fired = false;
        vm.SessionRequested += (_, _) => fired = true;

        vm.OpenSessionCommand.Execute(RemoteProtocol.Ssh);

        Assert.False(fired);
    }
}
