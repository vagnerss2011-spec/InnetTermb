using System.Collections.ObjectModel;
using RemoteOps.Desktop.NDesk;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.Terminal;

namespace RemoteOps.Desktop.ViewModels;

public sealed class TabsViewModel : BaseViewModel
{
    private SessionTabViewModel? _activeTab;

    public TabsViewModel()
    {
        CloseTabCommand = new RelayCommand(
            obj => CloseTab(obj as SessionTabViewModel),
            obj => obj is SessionTabViewModel tab && !tab.IsPinned);

        CloseActiveTabCommand = new RelayCommand(
            () => CloseTab(ActiveTab),
            () => ActiveTab is { IsPinned: false });
    }

    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    public RelayCommand CloseTabCommand { get; }

    public RelayCommand CloseActiveTabCommand { get; }

    public SessionTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            Set(ref _activeTab, value);
            CloseActiveTabCommand.RaiseCanExecuteChanged();
        }
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

    /// <summary>Adiciona uma aba de terminal pré-construída e a ativa.</summary>
    public void OpenTerminalTab(TerminalTabViewModel tab)
    {
        Tabs.Add(tab);
        ActiveTab = tab;
        RaisePropertyChanged(nameof(HasTabs));
    }

    /// <summary>Adiciona uma aba RDP pré-construída e a ativa.</summary>
    public void OpenRdpTab(RdpTabViewModel tab)
    {
        Tabs.Add(tab);
        ActiveTab = tab;
        RaisePropertyChanged(nameof(HasTabs));
    }

    /// <summary>Adiciona a aba NDesk pré-construída (pinada) e a ativa.</summary>
    public void OpenNdeskTab(NDeskTabViewModel tab)
    {
        Tabs.Add(tab);
        ActiveTab = tab;
        RaisePropertyChanged(nameof(HasTabs));
    }

    private void CloseTab(SessionTabViewModel? tab)
    {
        if (tab == null || tab.IsPinned)
        {
            return;
        }

        // Close the underlying session (fire-and-forget; pump cancellation is fast)
        if (tab is TerminalTabViewModel ttvm)
            _ = ttvm.CloseAsync();
        else if (tab is RdpTabViewModel rtvm)
            _ = rtvm.CloseAsync();

        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, idx - 1)]
                : null;
        }

        RaisePropertyChanged(nameof(HasTabs));
        CloseActiveTabCommand.RaiseCanExecuteChanged();
    }
}
