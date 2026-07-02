using System.Threading.Tasks;
using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class NoPolicyFeedSourceTests
{
    [Fact]
    public async Task GetMinimumRequiredVersion_IsNull()
        => Assert.Null(await new NoPolicyFeedSource().GetMinimumRequiredVersionAsync());
}
