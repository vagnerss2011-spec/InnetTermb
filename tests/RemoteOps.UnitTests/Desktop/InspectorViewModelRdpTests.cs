using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class InspectorViewModelRdpTests
{
    private sealed class FixedFeatureFlags : IFeatureFlags
    {
        private readonly bool _enabled;
        public FixedFeatureFlags(bool enabled) => _enabled = enabled;
        public bool IsEnabled(string flagName) => flagName == FeatureFlagNames.RdpEnabled && _enabled;
    }

    private static AssetViewModel MakeAssetWithRdpEndpoint() => new(new Asset
    {
        Id = "asset-1",
        WorkspaceId = "ws-1",
        Name = "Host1",
        Endpoints = [new Endpoint { Id = "ep-1", AssetId = "asset-1", Protocol = "rdp", Port = 3389 }],
    });

    [Fact]
    public void CanOpenRdp_FlagOnAndHasRdpEndpoint_IsTrue()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore(), featureFlags: new FixedFeatureFlags(true))
        {
            Asset = MakeAssetWithRdpEndpoint(),
        };

        Assert.True(vm.CanOpenRdp);
    }

    [Fact]
    public void CanOpenRdp_FlagOff_IsFalse()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore(), featureFlags: new FixedFeatureFlags(false))
        {
            Asset = MakeAssetWithRdpEndpoint(),
        };

        Assert.False(vm.CanOpenRdp);
    }

    [Fact]
    public void CanOpenRdp_NoRdpEndpoint_IsFalse()
    {
        var assetVm = new AssetViewModel(new Asset { Id = "a", WorkspaceId = "ws", Name = "Host2", Endpoints = [] });
        var vm = new InspectorViewModel(new InMemoryLocalStore(), featureFlags: new FixedFeatureFlags(true))
        {
            Asset = assetVm,
        };

        Assert.False(vm.CanOpenRdp);
    }

    [Fact]
    public void CanOpenRdp_NoFeatureFlagsProvided_IsFalse()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore())
        {
            Asset = MakeAssetWithRdpEndpoint(),
        };

        Assert.False(vm.CanOpenRdp);
    }
}
