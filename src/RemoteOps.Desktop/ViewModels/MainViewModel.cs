using Microsoft.Extensions.DependencyInjection;

using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Terminal;
using RemoteOps.MikroTik;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// ViewModel raiz — orquestra as regiões da janela principal.
/// Injeta dependências nos ViewModels filhos e conecta os eventos entre eles.
/// </summary>
public sealed class MainViewModel : BaseViewModel
{
    private const string DefaultWorkspaceId = "ws-local";

    private readonly ITerminalSessionProvider? _sshProvider;
    private readonly ITerminalSessionProvider? _telnetProvider;

    private string _syncStatus = "Offline";
    private string _searchText = string.Empty;

    /// <summary>
    /// Ctor de produção. Os provedores SSH/Telnet são resolvidos pelo
    /// <c>AppCompositionRoot</c> como keyed services (por <see cref="RemoteProtocol"/>);
    /// o WinBox runner vem do INT-4.
    /// </summary>
    public MainViewModel(
        ILocalStore store,
        IWinBoxRunner? winBoxRunner = null,
        [FromKeyedServices(RemoteProtocol.Ssh)] ITerminalSessionProvider? sshProvider = null,
        [FromKeyedServices(RemoteProtocol.Telnet)] ITerminalSessionProvider? telnetProvider = null)
    {
        _sshProvider = sshProvider;
        _telnetProvider = telnetProvider;

        Sidebar = new SidebarViewModel(store, DefaultWorkspaceId);
        HostList = new HostListViewModel(store, DefaultWorkspaceId);
        Inspector = new InspectorViewModel(store, winBoxRunner);
        Tabs = new TabsViewModel();

        Sidebar.GroupSelected += (_, groupVm) =>
            _ = HostList.LoadAsync(groupVm?.Id);

        HostList.AssetSelected += (_, assetVm) =>
            Inspector.Asset = assetVm;

        Inspector.SessionRequested += OnSessionRequested;
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

    private void OnSessionRequested(object? sender, OpenSessionRequest req)
    {
        var provider = req.Protocol switch
        {
            RemoteProtocol.Ssh => _sshProvider,
            RemoteProtocol.Telnet => _telnetProvider,
            _ => null,
        };

        if (provider != null && req.EndpointId != null && req.CredentialRefId != null)
        {
            var sessionRequest = new SessionRequest
            {
                SessionId = Guid.NewGuid().ToString("n"),
                Protocol = req.Protocol,
                EndpointId = req.EndpointId,
                CredentialRefId = req.CredentialRefId,
            };

            var tab = new TerminalTabViewModel(
                id: sessionRequest.SessionId,
                title: $"{req.AssetName} ({req.Protocol.ToUpperInvariant()})",
                protocol: req.Protocol,
                provider: provider,
                baseRequest: sessionRequest);

            Tabs.OpenTerminalTab(tab);
        }
        else
        {
            // Fallback: placeholder tab for RDP/MikroTik or missing endpoint
            Tabs.OpenTab(req.AssetName, req.Protocol);
        }
    }
}
