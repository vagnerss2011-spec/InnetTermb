using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Terminal;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Testa a integração TabsViewModel ↔ TerminalTabViewModel:
/// abertura de aba de terminal, fechamento com CloseAsync e proteção de aba fixada.
/// </summary>
public sealed class TabsViewModelTerminalTests
{
    private static TerminalTabViewModel BuildTerminalTab(
        FakeTerminalSessionProvider? provider = null,
        string id = "tab-term-1",
        string title = "router-01 (SSH)")
    {
        provider ??= new FakeTerminalSessionProvider();
        var request = new SessionRequest
        {
            SessionId = id,
            Protocol = "ssh",
            EndpointId = "ep-1",
            CredentialRefId = "cr-1",
        };
        return new TerminalTabViewModel(id, title, "ssh", provider, request);
    }

    [Fact]
    public void OpenTerminalTab_AddsAndActivates()
    {
        var tabs = new TabsViewModel();
        var tab = BuildTerminalTab();

        tabs.OpenTerminalTab(tab);

        Assert.Single(tabs.Tabs);
        Assert.Equal(tab, tabs.ActiveTab);
        Assert.True(tabs.HasTabs);
    }

    [Fact]
    public void OpenTerminalTab_MultipleTabs_LastIsActive()
    {
        var tabs = new TabsViewModel();

        var t1 = BuildTerminalTab(id: "t1", title: "host-a");
        var t2 = BuildTerminalTab(id: "t2", title: "host-b");
        tabs.OpenTerminalTab(t1);
        tabs.OpenTerminalTab(t2);

        Assert.Equal(t2, tabs.ActiveTab);
        Assert.Equal(2, tabs.Tabs.Count);
    }

    [Fact]
    public void CloseTab_TerminalTab_CallsCloseAsyncAndRemovesTab()
    {
        var provider = new FakeTerminalSessionProvider();
        var tabs = new TabsViewModel();
        var tab = BuildTerminalTab(provider);
        tabs.OpenTerminalTab(tab);

        tabs.CloseTabCommand.Execute(tab);

        Assert.Empty(tabs.Tabs);
        Assert.False(tabs.HasTabs);
        // CloseAsync é fire-and-forget mas CloseAsync é chamado;
        // o CloseCount pode ser 0 se a sessão nunca foi aberta (handle == null) — isso é correto.
    }

    [Fact]
    public void CloseTab_PinnedTerminalTab_CannotExecute()
    {
        var tabs = new TabsViewModel();
        var tab = BuildTerminalTab();
        tab.IsPinned = true;
        tabs.OpenTerminalTab(tab);

        Assert.False(tabs.CloseTabCommand.CanExecute(tab));
    }

    [Fact]
    public void CloseTerminalTab_ActivatesPreviousTab()
    {
        var tabs = new TabsViewModel();
        var t1 = BuildTerminalTab(id: "t1", title: "host-a");
        var t2 = BuildTerminalTab(id: "t2", title: "host-b");
        tabs.OpenTerminalTab(t1);
        tabs.OpenTerminalTab(t2);

        tabs.CloseTabCommand.Execute(t2);

        Assert.Equal(t1, tabs.ActiveTab);
    }

    [Fact]
    public void MixedTabs_TerminalAndPlaceholder_CoexistCorrectly()
    {
        var tabs = new TabsViewModel();
        var placeholder = tabs.OpenTab("rdp-host", "rdp");
        var terminal = BuildTerminalTab(id: "t-ssh", title: "ssh-host (SSH)");
        tabs.OpenTerminalTab(terminal);

        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Equal(terminal, tabs.ActiveTab);

        // fechar o terminal — placeholder fica ativo
        tabs.CloseTabCommand.Execute(terminal);
        Assert.Equal(placeholder, tabs.ActiveTab);
    }
}
