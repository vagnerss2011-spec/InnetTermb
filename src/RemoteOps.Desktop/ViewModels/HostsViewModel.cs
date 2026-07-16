using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;

namespace RemoteOps.Desktop.ViewModels;

public sealed class HostsViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly SessionLauncher _launcher;
    private readonly IInlineCredentialService? _inlineCreds;
    private readonly string _workspaceId;
    private GroupCardViewModel? _currentGroup;
    private AssetViewModel? _selectedHost;
    private string _searchText = string.Empty;
    private string _selectedFilterKey = string.Empty; // "" = Todos; "role:X"; "vendor:X"

    public HostsViewModel(ILocalStore store, SessionLauncher launcher, string workspaceId, IInlineCredentialService? inlineCreds = null)
    {
        _store = store;
        _launcher = launcher;
        _inlineCreds = inlineCreds;
        _workspaceId = workspaceId;

        OpenGroupCommand = new RelayCommand(obj => { if (obj is GroupCardViewModel g) _ = OpenGroupAsync(g); });
        BackCommand = new RelayCommand(() => CurrentGroup = null, () => IsInGroup);
        ConnectPrimaryCommand = new RelayCommand(obj => { if (obj is AssetViewModel a) _ = ConnectAsync(a, _launcher.PrimaryProtocol(a.Asset)); });
        ConnectCommand = new RelayCommand(obj => { if (SelectedHost != null && obj is string p) _ = ConnectAsync(SelectedHost, p); }, _ => SelectedHost != null);
        NewGroupCommand = new RelayCommand(() => NewGroupRequested?.Invoke(this, EventArgs.Empty));
        NewHostCommand = new RelayCommand(() => NewHostRequested?.Invoke(this, CurrentGroup?.Id));
        EditHostCommand = new RelayCommand(() => { if (SelectedHost != null) EditHostRequested?.Invoke(this, SelectedHost); }, () => SelectedHost != null);
        DeleteHostCommand = new RelayCommand(() => _ = DeleteHostAsync(), () => SelectedHost != null);
        ApplyFilterCommand = new RelayCommand(obj => { if (obj is DeviceFilterChip c) ApplyFilter(c); });
    }

    public ObservableCollection<GroupCardViewModel> Groups { get; } = [];
    public ObservableCollection<AssetViewModel> Hosts { get; } = [];

    /// <summary>Chips de filtro por tipo/vendor — reconstruídos a partir dos hosts do grupo atual.</summary>
    public ObservableCollection<DeviceFilterChip> DeviceFilters { get; } = [];

    public RelayCommand OpenGroupCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ConnectPrimaryCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand NewGroupCommand { get; }
    public RelayCommand NewHostCommand { get; }
    public RelayCommand EditHostCommand { get; }
    public RelayCommand DeleteHostCommand { get; }
    public RelayCommand ApplyFilterCommand { get; }

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
            if (value is null)
            {
                Hosts.Clear();
                DeviceFilters.Clear();
                _selectedFilterKey = string.Empty;
            }
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
        if (_currentGroup is null) { DeviceFilters.Clear(); return; }
        var assets = await _store.GetAssetsAsync(_workspaceId, _currentGroup.Id);
        RebuildDeviceFilters(assets);
        var filter = _searchText.Trim();
        foreach (var a in assets)
        {
            if (!MatchesDeviceFilter(a)) continue;
            if (filter.Length > 0 && !a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !string.Join(" ", a.Tags).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Hosts.Add(new AssetViewModel(a));
        }
    }

    private void ApplyFilter(DeviceFilterChip chip)
    {
        _selectedFilterKey = chip.Key;
        foreach (var f in DeviceFilters) f.IsActive = ReferenceEquals(f, chip);
        _ = LoadHostsAsync();
    }

    /// <summary>
    /// Reconstrói os chips a partir dos hosts do grupo (papéis + vendors PRESENTES) — só aparece
    /// tipo/vendor que existe. Preserva a seleção atual se o chip ainda existir; senão volta a "Todos".
    /// </summary>
    private void RebuildDeviceFilters(IReadOnlyList<Asset> assets)
    {
        DeviceFilters.Clear();
        DeviceFilters.Add(new DeviceFilterChip(string.Empty, "Todos"));

        foreach (var role in assets.Select(a => a.DeviceRole)
                                   .Where(r => !string.IsNullOrEmpty(r))
                                   .Distinct()
                                   .OrderBy(DeviceCatalog.RoleLabel))
        {
            DeviceFilters.Add(new DeviceFilterChip($"role:{role}", DeviceCatalog.RoleLabel(role)));
        }

        foreach (var vk in assets.Select(VendorKeyOf)
                                 .Where(v => !string.IsNullOrEmpty(v))
                                 .Distinct()
                                 .OrderBy(v => v, StringComparer.Ordinal))
        {
            DeviceFilters.Add(new DeviceFilterChip($"vendor:{vk}", VendorDisplay(vk!)));
        }

        var active = DeviceFilters.FirstOrDefault(f => f.Key == _selectedFilterKey) ?? DeviceFilters[0];
        _selectedFilterKey = active.Key;
        active.IsActive = true;
    }

    private bool MatchesDeviceFilter(Asset a)
    {
        if (_selectedFilterKey.Length == 0) return true;
        if (_selectedFilterKey.StartsWith("role:", StringComparison.Ordinal))
            return a.DeviceRole == _selectedFilterKey["role:".Length..];
        if (_selectedFilterKey.StartsWith("vendor:", StringComparison.Ordinal))
            return VendorKeyOf(a) == _selectedFilterKey["vendor:".Length..];
        return true;
    }

    private static string? VendorKeyOf(Asset a) => DeviceClassifier
        .Suggest(a.Vendor, a.Model, a.Endpoints.Count > 0 ? a.Endpoints[0].Protocol : null).VendorKey;

    private static string VendorDisplay(string vendorKey)
        => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(vendorKey);

    private Task RefreshAsync() => IsInGroup ? LoadHostsAsync() : LoadAsync();

    private async Task DeleteHostAsync()
    {
        if (SelectedHost is null) return;
        // Disparado em fire-and-forget pelo command — envolve em try/catch e reporta via LaunchFailed
        // (como ConnectAsync). Sem isso, uma falha do cofre/DB ao revogar a senha inline ou apagar o
        // asset viraria exceção de Task descartada, SUMINDO da vista do operador (o app só captura
        // DispatcherUnhandledException/AppDomain, que não pegam Task fire-and-forget).
        try
        {
            // Apaga as credenciais INLINE dos endpoints do device (revoga o segredo no cofre) pra não
            // deixar envelope órfão; credenciais do Keychain (compartilhadas) NÃO são tocadas.
            if (_inlineCreds is not null)
            {
                foreach (var ep in SelectedHost.Asset.Endpoints)
                {
                    await _inlineCreds.DeleteForEndpointAsync(ep);
                }
            }
            await _store.DeleteAssetAsync(SelectedHost.Id);
            Hosts.Remove(SelectedHost);
            SelectedHost = null;
        }
        catch (Exception ex)
        {
            LaunchFailed?.Invoke(this, $"Falha ao excluir o host: {ex.Message}");
        }
    }

    public Task ReloadAfterEditAsync() => RefreshAsync();
}
