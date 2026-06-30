using RemoteOps.Desktop.Infrastructure;
using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// ViewModel raiz — orquestra as regiões da janela principal.
/// Injeta dependências nos ViewModels filhos e conecta os eventos entre eles.
/// </summary>
public sealed class MainViewModel : BaseViewModel
{
    private const string DefaultWorkspaceId = "ws-local";

    private string _syncStatus = "Offline";
    private string _searchText = string.Empty;

    public MainViewModel(ILocalStore store, IWinBoxRunner? winBoxRunner = null)
    {
        Sidebar = new SidebarViewModel(store, DefaultWorkspaceId);
        HostList = new HostListViewModel(store, DefaultWorkspaceId);
        Inspector = new InspectorViewModel(store, winBoxRunner);
        Tabs = new TabsViewModel();

        // Quando um grupo é selecionado na sidebar, filtra a lista de hosts.
        Sidebar.GroupSelected += (_, groupVm) =>
            _ = HostList.LoadAsync(groupVm?.Id);

        // Quando um host é selecionado, popula o inspector.
        HostList.AssetSelected += (_, assetVm) =>
            Inspector.Asset = assetVm;

        // Quando o inspector solicita abrir sessão, cria uma aba.
        Inspector.SessionRequested += (_, req) =>
            Tabs.OpenTab(req.AssetName, req.Protocol);
    }

    public SidebarViewModel Sidebar { get; }
    public HostListViewModel HostList { get; }
    public InspectorViewModel Inspector { get; }
    public TabsViewModel Tabs { get; }

    public string SyncStatus
    {
        get => _syncStatus;
        set => Set(ref _syncStatus, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            Set(ref _searchText, value);
            HostList.FilterText = value;
        }
    }

    public async Task InitializeAsync()
    {
        await Sidebar.LoadAsync();
        await HostList.LoadAsync();
    }
}
