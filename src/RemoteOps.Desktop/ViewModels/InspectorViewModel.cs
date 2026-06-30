using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class InspectorViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private AssetViewModel? _asset;
    private string _newEndpointProtocol = RemoteProtocol.Ssh;
    private string _newEndpointAddress = string.Empty;
    private int _newEndpointPort = 22;
    private bool _isBusy;

    public InspectorViewModel(ILocalStore store)
    {
        _store = store;

        AddEndpointCommand = new RelayCommand(
            () => _ = AddEndpointAsync(),
            () => !IsBusy && Asset != null && !string.IsNullOrWhiteSpace(NewEndpointAddress));

        OpenSessionCommand = new RelayCommand(
            obj => RequestOpenSession(obj as string ?? NewEndpointProtocol),
            _ => Asset != null);
    }

    public RelayCommand AddEndpointCommand { get; }
    public RelayCommand OpenSessionCommand { get; }

    public AssetViewModel? Asset
    {
        get => _asset;
        set
        {
            Set(ref _asset, value);
            RaisePropertyChanged(nameof(HasAsset));
            AddEndpointCommand.RaiseCanExecuteChanged();
            OpenSessionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasAsset => _asset != null;

    public string NewEndpointProtocol
    {
        get => _newEndpointProtocol;
        set
        {
            Set(ref _newEndpointProtocol, value);
            // Porta padrão por protocolo
            NewEndpointPort = value switch
            {
                RemoteProtocol.Ssh => 22,
                RemoteProtocol.Telnet => 23,
                RemoteProtocol.Rdp => 3389,
                _ => NewEndpointPort,
            };
        }
    }

    public string NewEndpointAddress
    {
        get => _newEndpointAddress;
        set
        {
            Set(ref _newEndpointAddress, value);
            AddEndpointCommand.RaiseCanExecuteChanged();
        }
    }

    public int NewEndpointPort
    {
        get => _newEndpointPort;
        set => Set(ref _newEndpointPort, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            Set(ref _isBusy, value);
            AddEndpointCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Disparado pelo inspector para que o MainViewModel abra uma aba.</summary>
    public event EventHandler<OpenSessionRequest>? SessionRequested;

    public async Task AddEndpointAsync()
    {
        var assetVm = Asset;
        if (assetVm == null || string.IsNullOrWhiteSpace(NewEndpointAddress))
        {
            return;
        }

        IsBusy = true;
        try
        {
            // Endereço: detectar FQDN vs IPv4 vs IPv6 pela família do IP.
            bool isIp = System.Net.IPAddress.TryParse(NewEndpointAddress, out var parsedIp);
            bool isIpv6 = isIp && parsedIp!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            bool isIpv4 = isIp && parsedIp!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
            var ep = new Endpoint
            {
                Id = Guid.NewGuid().ToString("n"),
                AssetId = assetVm.Id,
                Protocol = NewEndpointProtocol,
                Port = NewEndpointPort,
                Ipv4 = isIpv4 ? NewEndpointAddress : null,
                Ipv6 = isIpv6 ? NewEndpointAddress : null,
                Fqdn = isIp ? null : NewEndpointAddress,
            };

            await _store.AddEndpointAsync(ep);

            // Reflete o endpoint recém-criado no AssetViewModel exibido (DataGrid/Inspector),
            // sem exigir reload da lista.
            var updated = await _store.GetAssetAsync(assetVm.Id);
            if (updated != null)
            {
                assetVm.Refresh(updated);
            }

            NewEndpointAddress = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RequestOpenSession(string protocol)
    {
        if (Asset == null)
        {
            return;
        }

        SessionRequested?.Invoke(this, new OpenSessionRequest
        {
            AssetId = Asset.Id,
            AssetName = Asset.Name,
            Protocol = protocol,
        });
    }
}

public sealed class OpenSessionRequest
{
    public required string AssetId { get; init; }
    public required string AssetName { get; init; }
    public required string Protocol { get; init; }
}
