using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class GroupCardViewModelTests
{
    [Fact]
    public void HostCountLabel_FormatsCount()
    {
        Assert.Equal("1 host", new GroupCardViewModel("g1", "Innet", 1).HostCountLabel);
        Assert.Equal("10 hosts", new GroupCardViewModel("g2", "Serra", 10).HostCountLabel);
    }
}
