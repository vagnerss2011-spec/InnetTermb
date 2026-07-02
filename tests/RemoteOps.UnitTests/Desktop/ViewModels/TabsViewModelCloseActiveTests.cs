using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class TabsViewModelCloseActiveTests
{
    [Fact]
    public void CloseActiveTab_RemovesActiveTab()
    {
        var tabs = new TabsViewModel();
        tabs.OpenTab("r1", "ssh");
        Assert.True(tabs.HasTabs);

        tabs.CloseActiveTabCommand.Execute(null);

        Assert.False(tabs.HasTabs);
    }

    [Fact]
    public void CloseActiveTab_DisabledWhenNoTab()
    {
        var tabs = new TabsViewModel();
        Assert.False(tabs.CloseActiveTabCommand.CanExecute(null));
    }
}
