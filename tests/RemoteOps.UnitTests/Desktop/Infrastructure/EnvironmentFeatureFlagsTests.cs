using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class EnvironmentFeatureFlagsTests
{
    [Fact]
    public void IsEnabled_NoFlagsConfigured_ReturnsFalse()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "");
        Assert.False(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void IsEnabled_FlagListed_ReturnsTrue()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "rdp.enabled");
        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void IsEnabled_MultipleFlags_CommaSeparated_ParsesAll()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "foo.bar, rdp.enabled ,baz");
        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
        Assert.True(flags.IsEnabled("foo.bar"));
        Assert.True(flags.IsEnabled("baz"));
    }

    [Fact]
    public void IsEnabled_IsCaseInsensitive()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "RDP.ENABLED");
        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void IsEnabled_UnknownFlag_ReturnsFalse()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "rdp.enabled");
        Assert.False(flags.IsEnabled("ndesk.enabled"));
    }
}
