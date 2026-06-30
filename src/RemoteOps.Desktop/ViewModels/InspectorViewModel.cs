using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.ViewModels;

public sealed class InspectorViewModel : BaseViewModel
{
    private readonly ILocalStore _store;
    private readonly IWinBoxRunner? _winBoxRunner;
    private AssetViewModel? _asset;
    private string _newEndpointProtocol = RemoteProtocol.Ssh;
    private string _newEndpointAddress = string.Empty;
    private int _newEndpointPort = 22;
    private bool _isBusy;
    private string? _winBoxError;

    public InspectorViewModel(ILocalStore store, IWinBoxRunner? winBoxRunner = null)
    {
        _store = store;
        _winBoxRunner = winBoxRunner;

        AddEndpointCommand = new RelayCommand(
            () => _ = AddEndpointAsync(),
            () => !IsBusy && Asset != null && !string.IsNullOrWhiteSpace(NewEndpointAddress));

        OpenSessionCommand = new RelayCommand(
            obj => RequestOpenSession(obj as string ?? NewEndpointProtocol),
            _ => Asset != null);

        OpenWinBoxCommand = new RelayCommand(
            () => _ = OpenWinBoxAsync(),
            () => _winBoxRunner != null && IsMikroTikHost && !IsBusy);
    }

    public RelayCommand AddEndpointCommand { get; }
    public RelayCommand OpenSessionCommand { get; }
    public RelayCommand OpenWinBoxCommand { get; }

    public AssetViewModel? Asset
    {
        get => _asset;
        set
        {
            Set(ref _asset, value);
            WinBoxError = null;
            RaisePropertyChanged(nameof(HasAsset));
            RaisePropertyChanged(nameof(IsMikroTikHost));
            AddEndpointCommand.RaiseCanExecuteChanged();
            OpenSessionCommand.RaiseCanExecuteChanged();
            OpenWinBoxCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasAsset => _asset != null;

    public bool IsMikroTikHost =>
        _asset?.Asset.Endpoints.Any(e => e.Protocol == RemoteProtocol.MikroTik) ?? false;

    public string? WinBoxError
    {
        get => _winBoxError;
        private set
        {
            Set(ref _winBoxError, value);
            RaisePropertyChanged(nameof(HasWinBoxError));
        }
    }

    public bool HasWinBoxError => !string.IsNullOrEmpty(_winBoxError);

    public string NewEndpointProtocol
    {
        get => _newEndpointProtocol;
        set
        {
            Set(ref _newEndpointProtocol, value);
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
            OpenWinBoxCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Disparado pelo inspector para que o MainViewModel abra uma aba.</summary>
    public event EventHandler<OpenSessionRequest>? SessionRequested;

    public async Task AddEndpointAsync()
    {
        var assetVm = Asset;
        if (assetVm == null || string.IsNullOrWhiteSpace(NewEndpointAddress))
            return;

        IsBusy = true;
        try
        {
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

            var updated = await _store.GetAssetAsync(assetVm.Id);
            if (updated != null)
                assetVm.Refresh(updated);

            NewEndpointAddress = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenWinBoxAsync()
    {
        if (_winBoxRunner == null || _asset == null)
            return;

        var ep = _asset.Asset.Endpoints.FirstOrDefault(e => e.Protocol == RemoteProtocol.MikroTik);
        if (ep == null)
            return;

        WinBoxError = null;
        IsBusy = true;
        try
        {
            var (address, addressFamily) = ResolveAddress(ep);

            var request = new ExternalToolLaunchRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                WorkspaceId = _asset.Asset.WorkspaceId,
                Tool = "winbox",
                HostId = _asset.Id,
                Target = new ExternalToolTarget
                {
                    Address = address,
                    AddressFamily = addressFamily,
                    Port = ep.Port,
                    PreferIpv6 = ep.PreferIpv6,
                },
                CredentialRefId = ep.CredentialRefId,
                IncludePasswordArgument = false,  // Modo A: sem senha automática por argumento
                RequestedBy = "local-user",
                RequestedAt = DateTimeOffset.UtcNow,
            };

            await _winBoxRunner.LaunchAsync(request);
        }
        catch (WinBoxValidationException ex)
        {
            WinBoxError = ex.Message;
        }
        catch (Exception ex)
        {
            WinBoxError = $"Erro ao abrir WinBox: {ex.GetType().Name}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (string address, string? addressFamily) ResolveAddress(Endpoint ep)
    {
        if (ep.PreferIpv6 && ep.Ipv6 != null)
            return (ep.Ipv6, "ipv6");
        if (ep.Ipv4 != null)
            return (ep.Ipv4, "ipv4");
        if (ep.Ipv6 != null)
            return (ep.Ipv6, "ipv6");
        return (ep.Fqdn ?? string.Empty, ep.Fqdn != null ? "dns" : null);
    }

    private void RequestOpenSession(string protocol)
    {
        if (Asset == null)
            return;

        var endpoint = Asset.Asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);

        SessionRequested?.Invoke(this, new OpenSessionRequest
        {
            AssetId = Asset.Id,
            AssetName = Asset.Name,
            Protocol = protocol,
            EndpointId = endpoint?.Id,
            CredentialRefId = endpoint?.CredentialRefId,
        });
    }
}

public sealed class OpenSessionRequest
{
    public required string AssetId { get; init; }
    public required string AssetName { get; init; }
    public required string Protocol { get; init; }
    public string? EndpointId { get; init; }
    public string? CredentialRefId { get; init; }
}
