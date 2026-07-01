using System.Threading.Tasks;

namespace RemoteOps.Desktop.ViewModels;

public sealed class WorkspaceViewModel : BaseViewModel
{
    public WorkspaceViewModel(BrowserViewModel browser, TabsViewModel tabs)
    {
        Browser = browser;
        Tabs = tabs;
    }

    public BrowserViewModel Browser { get; }
    public TabsViewModel Tabs { get; }

    public Task InitializeAsync() => Browser.Hosts.LoadAsync();
}
