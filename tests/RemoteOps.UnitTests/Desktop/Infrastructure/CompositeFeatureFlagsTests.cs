using System.Collections.Generic;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class CompositeFeatureFlagsTests
{
    private sealed class FakeStore : ISettingsStore
    {
        private AppSettings _s;
        public FakeStore(AppSettings s) => _s = s;
        public AppSettings Load() => _s;
        public void Save(AppSettings settings) => _s = settings;
    }

    [Fact]
    public void EnvEnabled_OverridesSettingsFalse()
    {
        var env = new EnvironmentFeatureFlags(rawFlags: FeatureFlagNames.RdpEnabled);
        var store = new FakeStore(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.RdpEnabled] = false },
        });
        var flags = new CompositeFeatureFlags(store, env);

        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void SettingsEnabled_WithEmptyEnv_ReturnsTrue()
    {
        var env = new EnvironmentFeatureFlags(rawFlags: "");
        var store = new FakeStore(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.NdeskEnabled] = true },
        });
        var flags = new CompositeFeatureFlags(store, env);

        Assert.True(flags.IsEnabled(FeatureFlagNames.NdeskEnabled));
    }

    [Fact]
    public void BothOff_ReturnsFalse()
    {
        var env = new EnvironmentFeatureFlags(rawFlags: "");
        var store = new FakeStore(new AppSettings());
        var flags = new CompositeFeatureFlags(store, env);

        Assert.False(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }
}
