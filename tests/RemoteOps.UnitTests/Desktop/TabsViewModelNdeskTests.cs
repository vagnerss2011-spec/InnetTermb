using RemoteOps.Desktop.NDesk;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class TabsViewModelNdeskTests
{
    [Fact]
    public void OpenNdeskTab_AddsAndActivatesTab_Pinned()
    {
        var tabs = new TabsViewModel();
        var tab = new NDeskTabViewModel(new LoopbackNDeskBrokerClient());

        tabs.OpenNdeskTab(tab);

        Assert.Contains(tab, tabs.Tabs);
        Assert.Same(tab, tabs.ActiveTab);
        Assert.True(tabs.HasTabs);
        Assert.False(tabs.CloseTabCommand.CanExecute(tab));
    }
}
