using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("0.10.0", 0, 10, 0, null)]
    [InlineData("0.10.0-beta.1", 0, 10, 0, "beta.1")]
    [InlineData("2.0.0-rc.2", 2, 0, 0, "rc.2")]
    public void Parse_ValidVersion_ExtractsComponents(
        string raw, int major, int minor, int patch, string? preRelease)
    {
        AppVersion version = AppVersion.Parse(raw);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    public void TryParse_InvalidVersion_ReturnsFalse(string raw)
    {
        bool ok = AppVersion.TryParse(raw, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        bool ok = AppVersion.TryParse(null, out _);
        Assert.False(ok);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.3", "1.3.0")]
    [InlineData("1.2.3", "2.0.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0-alpha.1", "1.0.0-beta.1")]
    public void CompareTo_LowerVersion_IsLessThanHigherVersion(string lower, string higher)
    {
        AppVersion left = AppVersion.Parse(lower);
        AppVersion right = AppVersion.Parse(higher);

        Assert.True(left.CompareTo(right) < 0);
        Assert.True(right.CompareTo(left) > 0);
    }

    [Fact]
    public void CompareTo_EqualVersions_ReturnsZero()
    {
        AppVersion left = AppVersion.Parse("1.2.3-beta.1");
        AppVersion right = AppVersion.Parse("1.2.3-beta.1");

        Assert.Equal(0, left.CompareTo(right));
    }

    [Fact]
    public void ToString_RoundTripsFormattedValue()
    {
        Assert.Equal("1.2.3", AppVersion.Parse("1.2.3").ToString());
        Assert.Equal("1.2.3-beta.1", AppVersion.Parse("1.2.3-beta.1").ToString());
    }
}
