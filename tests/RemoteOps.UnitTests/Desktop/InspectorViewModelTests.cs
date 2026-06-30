using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.MikroTik;

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
    public async Task AddEndpoint_StoresEndpointAndRefreshesAsset()
    {
        var (vm, asset) = await BuildWithAsset();
        vm.NewEndpointProtocol = RemoteProtocol.Ssh;
        vm.NewEndpointAddress = "192.168.1.1";
        vm.NewEndpointPort = 22;

        await vm.AddEndpointAsync();

        Assert.Equal(string.Empty, vm.NewEndpointAddress);
        // O AssetViewModel exibido deve refletir o endpoint recém-criado (sem reload).
        var ep = Assert.Single(asset.Asset.Endpoints);
        Assert.Equal(RemoteProtocol.Ssh, ep.Protocol);
        Assert.Equal("192.168.1.1", ep.Ipv4);
        Assert.Null(ep.Ipv6);
        Assert.Null(ep.Fqdn);
        Assert.Equal(22, ep.Port);
        Assert.Equal("192.168.1.1", asset.PrimaryAddress);
    }

    [Fact]
    public async Task AddEndpoint_Ipv6Address_GoesToIpv6Field()
    {
        var (vm, asset) = await BuildWithAsset();
        vm.NewEndpointProtocol = RemoteProtocol.Ssh;
        vm.NewEndpointAddress = "2001:db8::1";

        await vm.AddEndpointAsync();

        var ep = Assert.Single(asset.Asset.Endpoints);
        Assert.Equal("2001:db8::1", ep.Ipv6);
        Assert.Null(ep.Ipv4);
        Assert.Null(ep.Fqdn);
    }

    [Fact]
    public async Task AddEndpoint_Fqdn_GoesToFqdnField()
    {
        var (vm, asset) = await BuildWithAsset();
        vm.NewEndpointAddress = "router.example.com";

        await vm.AddEndpointAsync();

        var ep = Assert.Single(asset.Asset.Endpoints);
        Assert.Equal("router.example.com", ep.Fqdn);
        Assert.Null(ep.Ipv4);
        Assert.Null(ep.Ipv6);
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

    // ── OpenWinBoxCommand ────────────────────────────────────────────────────────

    [Fact]
    public void OpenWinBox_NoRunner_CannotExecute()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore(), winBoxRunner: null);
        Assert.False(vm.OpenWinBoxCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenWinBox_NoMikroTikEndpoint_CannotExecute()
    {
        var store = new InMemoryLocalStore();
        var asset = await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-test", Name = "router" });
        var vm = new InspectorViewModel(store, new FakeWinBoxRunner());
        vm.Asset = new AssetViewModel(asset);

        Assert.False(vm.IsMikroTikHost);
        Assert.False(vm.OpenWinBoxCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenWinBox_MikroTikEndpoint_IsMikroTikHostTrue()
    {
        var (vm, _) = await BuildWithMikroTikEndpoint();

        Assert.True(vm.IsMikroTikHost);
        Assert.True(vm.OpenWinBoxCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenWinBox_ValidationException_SetsWinBoxError()
    {
        var runner = new FakeWinBoxRunner(
            _ => throw new WinBoxValidationException("Hash inválido — execução bloqueada."));
        var (vm, _) = await BuildWithMikroTikEndpoint(runner);

        await vm.OpenWinBoxAsync();

        Assert.True(vm.HasWinBoxError);
        Assert.Contains("Hash inválido", vm.WinBoxError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenWinBox_Success_ClearsError()
    {
        var runner = new FakeWinBoxRunner(_ => Task.FromResult("launch-id-01"));
        var (vm, _) = await BuildWithMikroTikEndpoint(runner);

        await vm.OpenWinBoxAsync();

        Assert.False(vm.HasWinBoxError);
        Assert.Null(vm.WinBoxError);
    }

    [Fact]
    public async Task OpenWinBox_Success_BuildsRequestWithCorrectAddress()
    {
        ExternalToolLaunchRequest? captured = null;
        var runner = new FakeWinBoxRunner(req =>
        {
            captured = req;
            return Task.FromResult("launch-id-01");
        });
        var (vm, _) = await BuildWithMikroTikEndpoint(runner, ipv4: "192.168.1.1");

        await vm.OpenWinBoxAsync();

        Assert.NotNull(captured);
        Assert.Equal("192.168.1.1", captured!.Target.Address);
        Assert.Equal("ipv4", captured.Target.AddressFamily);
        Assert.Equal("winbox", captured.Tool);
        Assert.False(captured.IncludePasswordArgument);
    }

    [Fact]
    public async Task OpenWinBox_Ipv6Endpoint_AddressHasIpv6Family()
    {
        ExternalToolLaunchRequest? captured = null;
        var runner = new FakeWinBoxRunner(req =>
        {
            captured = req;
            return Task.FromResult("id");
        });
        // Explicit ipv4: null so only IPv6 is available — PreferIpv6 = true in the helper.
        var (vm, _) = await BuildWithMikroTikEndpoint(runner, ipv4: null, ipv6: "2001:db8::1");

        await vm.OpenWinBoxAsync();

        Assert.NotNull(captured);
        Assert.Equal("2001:db8::1", captured!.Target.Address);
        Assert.Equal("ipv6", captured.Target.AddressFamily);
    }

    [Fact]
    public async Task OpenWinBox_SelectNewAsset_ClearsWinBoxError()
    {
        var runner = new FakeWinBoxRunner(
            _ => throw new WinBoxValidationException("bloqueado"));
        var (vm, _) = await BuildWithMikroTikEndpoint(runner);
        await vm.OpenWinBoxAsync();
        Assert.True(vm.HasWinBoxError);

        // Selecionar um novo asset deve limpar o erro anterior.
        vm.Asset = null;

        Assert.False(vm.HasWinBoxError);
        Assert.Null(vm.WinBoxError);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<(InspectorViewModel Vm, AssetViewModel Asset)> BuildWithMikroTikEndpoint(
        FakeWinBoxRunner? runner = null,
        string? ipv4 = "10.0.0.1",
        string? ipv6 = null)
    {
        var store = new InMemoryLocalStore();
        var asset = await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-test", Name = "mt-01" });
        await store.AddEndpointAsync(new Endpoint
        {
            Id = Guid.NewGuid().ToString("n"),
            AssetId = asset.Id,
            Protocol = RemoteProtocol.MikroTik,
            Ipv4 = ipv4,
            Ipv6 = ipv6,
            Port = 8291,
            PreferIpv6 = ipv6 != null && ipv4 == null,
        });
        var updated = (await store.GetAssetAsync(asset.Id))!;
        var vm = new InspectorViewModel(store, runner ?? new FakeWinBoxRunner());
        var assetVm = new AssetViewModel(updated);
        vm.Asset = assetVm;
        return (vm, assetVm);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────

    private sealed class FakeWinBoxRunner : IWinBoxRunner
    {
        private readonly Func<ExternalToolLaunchRequest, Task<string>> _handler;

        public FakeWinBoxRunner(Func<ExternalToolLaunchRequest, Task<string>>? handler = null)
        {
            _handler = handler ?? (_ => Task.FromResult("fake-launch-id"));
        }

        public Task<string> LaunchAsync(ExternalToolLaunchRequest request, CancellationToken ct = default)
            => _handler(request);
    }
}
