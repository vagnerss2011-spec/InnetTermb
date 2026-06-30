using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class MainViewModelRdpTests
{
    private sealed class FixedFeatureFlags : IFeatureFlags
    {
        private readonly bool _enabled;
        public FixedFeatureFlags(bool enabled) => _enabled = enabled;
        public bool IsEnabled(string flagName) => flagName == FeatureFlagNames.RdpEnabled && _enabled;
    }

    [Fact]
    public void OnSessionRequested_RdpWithFlagOn_OpensRdpTab()
    {
        var store = new RemoteOps.Desktop.Infrastructure.InMemoryLocalStore();
        var rdpProvider = new FakeRdpSessionProvider();
        var rdpCredResolver = new FakeRdpCredentialResolver();
        var vm = new MainViewModel(
            store,
            featureFlags: new FixedFeatureFlags(enabled: true),
            rdpProvider: rdpProvider,
            rdpCredentialResolver: rdpCredResolver);

        InvokeSessionRequested(vm, "rdp", endpointId: "ep-1", credentialRefId: "cr-1");

        Assert.IsType<RdpTabViewModel>(vm.Tabs.ActiveTab);
    }

    [Fact]
    public void OnSessionRequested_RdpWithFlagOff_FallsBackToPlaceholder()
    {
        var store = new RemoteOps.Desktop.Infrastructure.InMemoryLocalStore();
        var rdpProvider = new FakeRdpSessionProvider();
        var rdpCredResolver = new FakeRdpCredentialResolver();
        var vm = new MainViewModel(
            store,
            featureFlags: new FixedFeatureFlags(enabled: false),
            rdpProvider: rdpProvider,
            rdpCredentialResolver: rdpCredResolver);

        InvokeSessionRequested(vm, "rdp", endpointId: "ep-1", credentialRefId: "cr-1");

        Assert.IsNotType<RdpTabViewModel>(vm.Tabs.ActiveTab);
        Assert.NotNull(vm.Tabs.ActiveTab);
    }

    [Fact]
    public void OnSessionRequested_RdpMissingProvider_FallsBackToPlaceholder()
    {
        var store = new RemoteOps.Desktop.Infrastructure.InMemoryLocalStore();
        var vm = new MainViewModel(store, featureFlags: new FixedFeatureFlags(enabled: true));

        InvokeSessionRequested(vm, "rdp", endpointId: "ep-1", credentialRefId: "cr-1");

        Assert.IsNotType<RdpTabViewModel>(vm.Tabs.ActiveTab);
    }

    private static void InvokeSessionRequested(MainViewModel vm, string protocol, string? endpointId, string? credentialRefId)
    {
        var method = typeof(MainViewModel).GetMethod("OnSessionRequested",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, [vm, new OpenSessionRequest
        {
            AssetId = "asset-1",
            AssetName = "Host1",
            Protocol = protocol,
            EndpointId = endpointId,
            CredentialRefId = credentialRefId,
        }]);
    }
}
