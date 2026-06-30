using System.Collections.ObjectModel;

namespace RemoteOps.Desktop.ViewModels;

public sealed class TabsViewModel : BaseViewModel
{
    private SessionTabViewModel? _activeTab;

    public TabsViewModel()
    {
        CloseTabCommand = new RelayCommand(
            obj => CloseTab(obj as SessionTabViewModel),
            obj => obj is SessionTabViewModel tab && !tab.IsPinned);
    }

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    public RelayCommand CloseTabCommand { get; }

    public SessionTabViewModel? ActiveTab
    {
        get => _activeTab;
        set => Set(ref _activeTab, value);
    }

    public bool HasTabs => Tabs.Count > 0;

    public SessionTabViewModel OpenTab(string assetName, string protocol)
    {
        var tab = new SessionTabViewModel(
            id: Guid.NewGuid().ToString("n"),
            title: $"{assetName} ({protocol.ToUpperInvariant()})",
            protocol: protocol);

        Tabs.Add(tab);
        ActiveTab = tab;
        RaisePropertyChanged(nameof(HasTabs));
        return tab;
    }

    private void CloseTab(SessionTabViewModel? tab)
    {
        if (tab == null || tab.IsPinned)
        {
            return;
        }

        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, idx - 1)]
                : null;
        }

        RaisePropertyChanged(nameof(HasTabs));
    }
}
