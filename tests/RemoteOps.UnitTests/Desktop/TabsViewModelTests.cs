using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class TabsViewModelTests
{
    [Fact]
    public void OpenTab_AddsAndActivates()
    {
        var vm = new TabsViewModel();

        var tab = vm.OpenTab("router-01", RemoteProtocol.Ssh);

        Assert.Single(vm.Tabs);
        Assert.Equal(tab, vm.ActiveTab);
        Assert.True(vm.HasTabs);
    }

    [Fact]
    public void OpenTab_MultipleActivatesLast()
    {
        var vm = new TabsViewModel();
        vm.OpenTab("host-a", RemoteProtocol.Ssh);
        var last = vm.OpenTab("host-b", RemoteProtocol.Rdp);

        Assert.Equal(last, vm.ActiveTab);
    }

    [Fact]
    public void CloseTab_RemovesTab()
    {
        var vm = new TabsViewModel();
        var tab = vm.OpenTab("fw-01", RemoteProtocol.Ssh);

        vm.CloseTabCommand.Execute(tab);

        Assert.Empty(vm.Tabs);
        Assert.False(vm.HasTabs);
    }

    [Fact]
    public void CloseTab_Pinned_CannotExecute()
    {
        var vm = new TabsViewModel();
        var tab = vm.OpenTab("infra", RemoteProtocol.Ssh);
        tab.IsPinned = true;

        Assert.False(vm.CloseTabCommand.CanExecute(tab));
    }

    [Fact]
    public void CloseActive_ActivatesPrevious()
    {
        var vm = new TabsViewModel();
        var first = vm.OpenTab("host-a", RemoteProtocol.Ssh);
        var second = vm.OpenTab("host-b", RemoteProtocol.Rdp);

        vm.CloseTabCommand.Execute(second);

        Assert.Equal(first, vm.ActiveTab);
    }

    [Fact]
    public void TabTitle_ContainsAssetNameAndProtocol()
    {
        var vm = new TabsViewModel();
        var tab = vm.OpenTab("core-switch", RemoteProtocol.Telnet);

        Assert.Contains("core-switch", tab.Title);
        Assert.Contains("TELNET", tab.Title);
    }
}
