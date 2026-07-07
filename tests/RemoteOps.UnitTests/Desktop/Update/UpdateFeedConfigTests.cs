using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class UpdateFeedConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRepoUrl_EmptyEnv_ReturnsDefault(string? env)
        => Assert.Equal(UpdateFeedConfig.DefaultRepoUrl, UpdateFeedConfig.ResolveRepoUrl(env));

    [Fact]
    public void ResolveRepoUrl_EnvSet_ReturnsEnvTrimmed()
        => Assert.Equal("https://github.com/acme/x", UpdateFeedConfig.ResolveRepoUrl("  https://github.com/acme/x  "));

    [Fact]
    public void DefaultRepoUrl_PointsAtPublicReleasesRepo()
        => Assert.Equal("https://github.com/vagnerss2011-spec/InnetTermb-releases", UpdateFeedConfig.DefaultRepoUrl);
}
