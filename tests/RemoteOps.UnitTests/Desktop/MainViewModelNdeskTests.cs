using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.NDesk;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class MainViewModelNdeskTests
{
    private sealed class FixedFeatureFlags : IFeatureFlags
    {
        private readonly bool _enabled;
        public FixedFeatureFlags(bool enabled) => _enabled = enabled;
        public bool IsEnabled(string flagName) => flagName == FeatureFlagNames.NdeskEnabled && _enabled;
    }

    [Fact]
    public void Ctor_FlagOnWithBroker_OpensPinnedNdeskTabAsActive()
    {
        var store = new InMemoryLocalStore();

        var vm = new MainViewModel(
            store,
            featureFlags: new FixedFeatureFlags(enabled: true),
            ndeskBrokerClient: new LoopbackNDeskBrokerClient());

        Assert.IsType<NDeskTabViewModel>(vm.Tabs.ActiveTab);
        Assert.True(((NDeskTabViewModel)vm.Tabs.ActiveTab!).IsPinned);
    }

    [Fact]
    public void Ctor_FlagOff_DoesNotOpenNdeskTab()
    {
        var store = new InMemoryLocalStore();

        var vm = new MainViewModel(
            store,
            featureFlags: new FixedFeatureFlags(enabled: false),
            ndeskBrokerClient: new LoopbackNDeskBrokerClient());

        Assert.Null(vm.Tabs.ActiveTab);
    }

    [Fact]
    public void Ctor_FlagOnWithoutBroker_DoesNotOpenNdeskTab()
    {
        var store = new InMemoryLocalStore();

        var vm = new MainViewModel(store, featureFlags: new FixedFeatureFlags(enabled: true));

        Assert.Null(vm.Tabs.ActiveTab);
    }
}
