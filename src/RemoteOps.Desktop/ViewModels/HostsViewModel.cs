using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;

namespace RemoteOps.Desktop.ViewModels;

public sealed class HostsViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly SessionLauncher _launcher;
    private readonly string _workspaceId;
    private GroupCardViewModel? _currentGroup;
    private AssetViewModel? _selectedHost;
    private string _searchText = string.Empty;

    public HostsViewModel(ILocalStore store, SessionLauncher launcher, string workspaceId)
    {
        _store = store;
        _launcher = launcher;
        _workspaceId = workspaceId;

        OpenGroupCommand = new RelayCommand(obj => { if (obj is GroupCardViewModel g) _ = OpenGroupAsync(g); });
        BackCommand = new RelayCommand(() => CurrentGroup = null, () => IsInGroup);
        ConnectPrimaryCommand = new RelayCommand(obj => { if (obj is AssetViewModel a) _ = ConnectAsync(a, _launcher.PrimaryProtocol(a.Asset)); });
        ConnectCommand = new RelayCommand(obj => { if (SelectedHost != null && obj is string p) _ = ConnectAsync(SelectedHost, p); }, _ => SelectedHost != null);
        NewGroupCommand = new RelayCommand(() => NewGroupRequested?.Invoke(this, EventArgs.Empty));
        NewHostCommand = new RelayCommand(() => NewHostRequested?.Invoke(this, CurrentGroup?.Id));
        EditHostCommand = new RelayCommand(() => { if (SelectedHost != null) EditHostRequested?.Invoke(this, SelectedHost); }, () => SelectedHost != null);
        DeleteHostCommand = new RelayCommand(() => _ = DeleteHostAsync(), () => SelectedHost != null);
    }

    public ObservableCollection<GroupCardViewModel> Groups { get; } = [];
    public ObservableCollection<AssetViewModel> Hosts { get; } = [];

    public RelayCommand OpenGroupCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ConnectPrimaryCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand NewGroupCommand { get; }
    public RelayCommand NewHostCommand { get; }
    public RelayCommand EditHostCommand { get; }
    public RelayCommand DeleteHostCommand { get; }

    public event EventHandler<string?>? NewHostRequested;
    public event EventHandler<AssetViewModel>? EditHostRequested;
    public event EventHandler? NewGroupRequested;

    /// <summary>Falha de conexão com mensagem acionável (MainWindow mostra ao operador).</summary>
    public event EventHandler<string>? LaunchFailed;

    /// <summary>
    /// Conecta observando o resultado — antes o command descartava a Task
    /// (`_ = LaunchAsync(...)`) e qualquer falha/exceção morria sem UI.
    /// </summary>
    public async Task ConnectAsync(AssetViewModel host, string protocol)
    {
        try
        {
            LaunchResult result = await _launcher.LaunchAsync(host.Asset, protocol);
            if (!result.Success && result.Error is { } error)
            {
                LaunchFailed?.Invoke(this, error);
            }
        }
        catch (Exception ex)
        {
            LaunchFailed?.Invoke(this, $"Falha inesperada ao conectar: {ex.Message}");
        }
    }

    public GroupCardViewModel? CurrentGroup
    {
        get => _currentGroup;
        private set
        {
            Set(ref _currentGroup, value);
            RaisePropertyChanged(nameof(IsInGroup));
            RaisePropertyChanged(nameof(BreadcrumbLabel));
            BackCommand.RaiseCanExecuteChanged();
            if (value is null) Hosts.Clear();
        }
    }

    public bool IsInGroup => _currentGroup != null;
    public string BreadcrumbLabel => _currentGroup is null ? "Grupos" : $"Grupos / {_currentGroup.Name}";

    public AssetViewModel? SelectedHost
    {
        get => _selectedHost;
        set
        {
            Set(ref _selectedHost, value);
            ConnectCommand.RaiseCanExecuteChanged();
            EditHostCommand.RaiseCanExecuteChanged();
            DeleteHostCommand.RaiseCanExecuteChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { Set(ref _searchText, value); _ = RefreshAsync(); }
    }

    public async Task LoadAsync()
    {
        CurrentGroup = null;
        Groups.Clear();
        var groups = await _store.GetGroupsAsync(_workspaceId);
        var filter = _searchText.Trim();
        foreach (var g in groups)
        {
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var count = (await _store.GetAssetsAsync(_workspaceId, g.Id)).Count;
            Groups.Add(new GroupCardViewModel(g.Id, g.Name, count));
        }
    }

    private async Task OpenGroupAsync(GroupCardViewModel group)
    {
        CurrentGroup = group;
        await LoadHostsAsync();
    }

    private async Task LoadHostsAsync()
    {
        Hosts.Clear();
        if (_currentGroup is null) return;
        var assets = await _store.GetAssetsAsync(_workspaceId, _currentGroup.Id);
        var filter = _searchText.Trim();
        foreach (var a in assets)
        {
            if (filter.Length > 0 && !a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !string.Join(" ", a.Tags).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Hosts.Add(new AssetViewModel(a));
        }
    }

    private Task RefreshAsync() => IsInGroup ? LoadHostsAsync() : LoadAsync();

    private async Task DeleteHostAsync()
    {
        if (SelectedHost is null) return;
        await _store.DeleteAssetAsync(SelectedHost.Id);
        Hosts.Remove(SelectedHost);
        SelectedHost = null;
    }

    public Task ReloadAfterEditAsync() => RefreshAsync();
}
