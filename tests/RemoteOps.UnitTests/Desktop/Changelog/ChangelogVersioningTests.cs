using RemoteOps.Desktop.Changelog;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Changelog;

public sealed class ChangelogVersioningTests
{
    [Theory]
    [InlineData("1.1.0", null, true)]      // nunca viu → novo
    [InlineData("1.1.0", "1.0.0", true)]   // maior que o visto → novo
    [InlineData("1.0.0", "1.0.0", false)]  // igual → não novo
    [InlineData("1.0.0", "1.1.0", false)]  // menor → não novo
    [InlineData("nao-semver", "1.0.0", false)] // inválido → não novo
    public void IsNewer_Works(string version, string? lastSeen, bool expected)
        => Assert.Equal(expected, ChangelogVersioning.IsNewer(version, lastSeen));

    [Fact]
    public void Latest_PicksHighestSemVer()
        => Assert.Equal("2.0.0", ChangelogVersioning.Latest(new[] { "1.9.0", "2.0.0", "1.10.0" }));

    [Fact]
    public void Latest_EmptyOrAllInvalid_ReturnsNull()
        => Assert.Null(ChangelogVersioning.Latest(new[] { "x", "y" }));
}
