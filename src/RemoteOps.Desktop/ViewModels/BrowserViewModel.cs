using System;

namespace RemoteOps.Desktop.ViewModels;

public enum BrowserSection { Hosts, Keychain, Logs }

public sealed class BrowserViewModel : BaseViewModel
{
    private BrowserSection _activeSection = BrowserSection.Hosts;

    public BrowserViewModel(HostsViewModel hosts, KeychainViewModel keychain, LogsViewModel logs)
    {
        Hosts = hosts;
        Keychain = keychain;
        Logs = logs;
        ShowHostsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Hosts);
        ShowKeychainCommand = new RelayCommand(() => { ActiveSection = BrowserSection.Keychain; _ = keychain.LoadAsync(); });
        ShowLogsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Logs);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        CheckUpdatesCommand = new RelayCommand(() => UpdatesRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
    }

    public HostsViewModel Hosts { get; }
    public KeychainViewModel Keychain { get; }
    public LogsViewModel Logs { get; }

    public BrowserSection ActiveSection
    {
        get => _activeSection;
        private set
        {
            Set(ref _activeSection, value);
            RaisePropertyChanged(nameof(IsHosts));
            RaisePropertyChanged(nameof(IsKeychain));
            RaisePropertyChanged(nameof(IsLogs));
        }
    }

    public bool IsHosts => _activeSection == BrowserSection.Hosts;
    public bool IsKeychain => _activeSection == BrowserSection.Keychain;
    public bool IsLogs => _activeSection == BrowserSection.Logs;

    public RelayCommand ShowHostsCommand { get; }
    public RelayCommand ShowKeychainCommand { get; }
    public RelayCommand ShowLogsCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand AboutCommand { get; }

    public event EventHandler? SettingsRequested;
    public event EventHandler? UpdatesRequested;
    public event EventHandler? AboutRequested;
}
