using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class TabsViewModelRdpTests
{
    private static RdpTabViewModel MakeTab(FakeRdpSessionProvider provider, FakeRdpCredentialResolver credResolver) =>
        new("id1", "Host (RDP)", "rdp", provider, credResolver, new SessionRequest
        {
            SessionId = "id1",
            Protocol = RemoteProtocol.Rdp,
            EndpointId = "ep-1",
            CredentialRefId = "cr-1",
        });

    [Fact]
    public void OpenRdpTab_AddsAndActivatesTab()
    {
        var tabs = new TabsViewModel();
        var tab = MakeTab(new FakeRdpSessionProvider(), new FakeRdpCredentialResolver());

        tabs.OpenRdpTab(tab);

        Assert.Contains(tab, tabs.Tabs);
        Assert.Same(tab, tabs.ActiveTab);
        Assert.True(tabs.HasTabs);
    }

    [Fact]
    public async Task CloseTab_OnRdpTab_CallsCloseAsync()
    {
        var tabs = new TabsViewModel();
        var provider = new FakeRdpSessionProvider();
        var tab = MakeTab(provider, new FakeRdpCredentialResolver());
        tabs.OpenRdpTab(tab);
        await tab.PrepareAsync();

        tabs.CloseTabCommand.Execute(tab);
        await Task.Delay(50); // CloseAsync é fire-and-forget, igual ao caminho Terminal

        Assert.Equal(1, provider.CloseCount);
        Assert.DoesNotContain(tab, tabs.Tabs);
    }
}
