using System.Linq;
using RemoteOps.Desktop.Changelog;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Changelog;

public sealed class EmbeddedChangelogSourceTests
{
    [Fact]
    public void Load_ReturnsSeededVersion_WithHighlights()
    {
        var source = new EmbeddedChangelogSource();
        var entries = source.Load();
        var v1 = entries.SingleOrDefault(e => e.Version == "1.0.0");
        Assert.NotNull(v1);
        Assert.NotEmpty(v1!.Highlights);
    }
}
