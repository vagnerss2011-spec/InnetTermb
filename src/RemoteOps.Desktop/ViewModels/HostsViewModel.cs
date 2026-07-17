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

    // Reentrância da reconciliação do sync (Fase 2): o sinal é debounced e marshalado pro Dispatcher,
    // mas a reconciliação tem awaits (lê o store) e um novo sinal pode chegar no meio. Sem guarda,
    // dois passes concorrentes mutariam as MESMAS ObservableCollections intercalados — o clássico
    // crash/duplicação de WPF. A guarda coalesce: se já está reconciliando, marca "faz de novo" e sai.
    private bool _reconcileInProgress;
    private bool _reconcilePending;

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

    // ── Reconciliação disparada pelo cloud sync (Fase 2) ─────────────────────────────────────

    /// <summary>
    /// Recarga "inteligente" chamada quando o sync APLICOU mudanças no store local (evento
    /// <c>SyncOrchestrator.ChangesApplied</c>). Re-busca do store e RECONCILIA as coleções por id em
    /// vez de recriá-las: um <see cref="LoadAsync"/> cru zeraria o grupo aberto, a seleção e o filtro
    /// (bug de runtime WPF que passa no build). Preserva:
    /// <list type="bullet">
    ///   <item>o grupo aberto (<see cref="CurrentGroup"/>) — a menos que o sync o tenha apagado;</item>
    ///   <item>o host selecionado — a MESMA instância continua na coleção, então o DataGrid mantém a seleção;</item>
    ///   <item>o chip de filtro ativo (<see cref="_selectedFilterKey"/>).</item>
    /// </list>
    ///
    /// <para><b>Afinidade de thread:</b> DEVE rodar na UI thread — muta <see cref="ObservableCollection{T}"/>
    /// com binding. O sinal vem da thread de sync; o App marshala pro Dispatcher antes de chamar aqui.</para>
    /// </summary>
    public async Task ReconcileFromStoreAsync()
    {
        // Coalesce reentrâncias (ver campos _reconcile*): um pass por vez, mas nunca perde o último sinal.
        if (_reconcileInProgress)
        {
            _reconcilePending = true;
            return;
        }

        _reconcileInProgress = true;
        try
        {
            do
            {
                _reconcilePending = false;
                await ReconcileOnceAsync();
            }
            while (_reconcilePending);
        }
        finally
        {
            _reconcileInProgress = false;
        }
    }

    private async Task ReconcileOnceAsync()
    {
        await ReconcileGroupsAsync();

        if (_currentGroup is null)
        {
            return; // na lista de grupos: reconciliar os cards basta.
        }

        // O grupo aberto pode ter sido APAGADO por outro device. Sumiu da lista → volta pros grupos
        // (o setter limpa Hosts e os filtros). Ficar "dentro" de um grupo inexistente travaria a UI.
        if (!GroupStillExists(_currentGroup.Id))
        {
            CurrentGroup = null;
            return;
        }

        await ReconcileHostsAsync();
    }

    private bool GroupStillExists(string id)
    {
        foreach (var g in Groups)
        {
            if (string.Equals(g.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reconcilia os cards de grupo (mesmo filtro/contagem do <see cref="LoadAsync"/>) por id.</summary>
    private async Task ReconcileGroupsAsync()
    {
        var groups = await _store.GetGroupsAsync(_workspaceId);
        var filter = _searchText.Trim();

        var desired = new List<GroupSnapshot>();
        foreach (var g in groups)
        {
            if (filter.Length > 0 && !g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var count = (await _store.GetAssetsAsync(_workspaceId, g.Id)).Count;
            desired.Add(new GroupSnapshot(g.Id, g.Name, count));
        }

        ReconcileById(
            Groups, desired,
            itemKey: c => c.Id,
            modelKey: s => s.Id,
            create: s => new GroupCardViewModel(s.Id, s.Name, s.Count),
            update: (card, s) => card.Update(s.Name, s.Count));
    }

    /// <summary>Reconcilia os hosts do grupo aberto (mesmos filtros do <see cref="LoadHostsAsync"/>) por id.</summary>
    private async Task ReconcileHostsAsync()
    {
        if (_currentGroup is null) return;

        var assets = await _store.GetAssetsAsync(_workspaceId, _currentGroup.Id);

        // Recria os chips PRESERVANDO o chip ativo (RebuildDeviceFilters já mantém _selectedFilterKey).
        RebuildDeviceFilters(assets);

        var filter = _searchText.Trim();
        var visible = assets.Where(a => MatchesDeviceFilter(a)
            && (filter.Length == 0
                || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || string.Join(" ", a.Tags).Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        ReconcileById(
            Hosts, visible,
            itemKey: h => h.Id,
            modelKey: a => a.Id,
            create: a => new AssetViewModel(a),
            update: (vm, a) => vm.Refresh(a));

        // Se o host selecionado saiu (apagado pelo sync ou filtrado), zera a seleção — sem um DataGrid
        // vivo (testes) ninguém faria isso por nós, e SelectedHost apontaria pra uma instância fora da lista.
        if (_selectedHost is not null && !Hosts.Contains(_selectedHost))
        {
            SelectedHost = null;
        }
    }

    /// <summary>
    /// Reconcilia uma <see cref="ObservableCollection{T}"/> COM a lista alvo por chave estável (id),
    /// preservando as INSTÂNCIAS que sobrevivem: remove os que sumiram, insere os novos na posição
    /// certa e move+atualiza os que ficaram. Preservar a instância é o que mantém a seleção do
    /// DataGrid e o estado dos cards — recriar tudo (o que <see cref="LoadAsync"/> faz) destruiria isso.
    /// Usa <see cref="ObservableCollection{T}.Move"/> (atômico) em vez de remover+inserir pra a seleção
    /// nunca "piscar" pra null no meio de um reordenamento.
    /// </summary>
    private static void ReconcileById<TItem, TModel>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TModel> desired,
        Func<TItem, string> itemKey,
        Func<TModel, string> modelKey,
        Func<TModel, TItem> create,
        Action<TItem, TModel> update)
    {
        // 1) Remover os que não estão mais no alvo.
        var desiredKeys = new HashSet<string>(desired.Select(modelKey), StringComparer.Ordinal);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(itemKey(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        // 2) Percorrer o alvo em ordem: inserir novos, mover+atualizar existentes pra a posição i.
        for (int i = 0; i < desired.Count; i++)
        {
            TModel model = desired[i];
            string key = modelKey(model);

            int current = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (string.Equals(itemKey(target[j]), key, StringComparison.Ordinal))
                {
                    current = j;
                    break;
                }
            }

            if (current < 0)
            {
                target.Insert(i, create(model));
            }
            else
            {
                if (current != i)
                {
                    target.Move(current, i); // preserva a instância (e a seleção do DataGrid)
                }

                update(target[i], model);
            }
        }
    }

    private readonly record struct GroupSnapshot(string Id, string Name, int Count);
}
