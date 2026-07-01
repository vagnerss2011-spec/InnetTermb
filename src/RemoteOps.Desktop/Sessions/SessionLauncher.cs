using System;
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.Terminal;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.MikroTik;
using RemoteOps.Rdp;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Sessions;

/// <summary>
/// Fonte única de "abrir um host por protocolo". Substitui a lógica antes espalhada
/// em MainViewModel.OnSessionRequested e InspectorViewModel.OpenWinBoxAsync.
/// </summary>
public sealed class SessionLauncher
{
    private static readonly string[] Preference = { RemoteProtocol.Ssh, RemoteProtocol.Telnet, RemoteProtocol.Rdp, RemoteProtocol.MikroTik };

    private readonly TabsViewModel _tabs;
    private readonly IWinBoxRunner? _winBox;
    private readonly IFeatureFlags? _flags;
    private readonly ITerminalSessionProvider? _ssh;
    private readonly ITerminalSessionProvider? _telnet;
    private readonly IRdpSessionProvider? _rdp;
    private readonly IRdpCredentialResolver? _rdpCred;

    public SessionLauncher(
        TabsViewModel tabs,
        IWinBoxRunner? winBox,
        IFeatureFlags? flags,
        ITerminalSessionProvider? ssh,
        ITerminalSessionProvider? telnet,
        IRdpSessionProvider? rdp,
        IRdpCredentialResolver? rdpCred)
    {
        _tabs = tabs;
        _winBox = winBox;
        _flags = flags;
        _ssh = ssh;
        _telnet = telnet;
        _rdp = rdp;
        _rdpCred = rdpCred;
    }

    public string PrimaryProtocol(Asset asset)
    {
        foreach (var p in Preference)
        {
            if (asset.Endpoints.Any(e => e.Protocol == p))
            {
                return p;
            }
        }
        return asset.Endpoints.Count > 0 ? asset.Endpoints[0].Protocol : RemoteProtocol.Ssh;
    }

    public bool CanLaunch(Asset asset, string protocol)
    {
        var ep = asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);
        if (ep is null)
        {
            return false;
        }
        return protocol switch
        {
            RemoteProtocol.Ssh => _ssh != null,
            RemoteProtocol.Telnet => _telnet != null,
            RemoteProtocol.Rdp => (_flags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false) && _rdp != null && _rdpCred != null,
            RemoteProtocol.MikroTik => _winBox != null,
            _ => false,
        };
    }

    public async Task LaunchAsync(Asset asset, string protocol)
    {
        var ep = asset.Endpoints.FirstOrDefault(e => e.Protocol == protocol);
        if (ep is null)
        {
            return;
        }

        if (protocol == RemoteProtocol.MikroTik)
        {
            await LaunchWinBoxAsync(asset, ep);
            return;
        }

        if (protocol == RemoteProtocol.Rdp)
        {
            if (!(_flags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false) || _rdp is null || _rdpCred is null || ep.CredentialRefId is null)
            {
                _tabs.OpenTab(asset.Name, protocol);
                return;
            }
            var req = new SessionRequest { SessionId = Guid.NewGuid().ToString("n"), Protocol = protocol, EndpointId = ep.Id, CredentialRefId = ep.CredentialRefId };
            _tabs.OpenRdpTab(new RdpTabViewModel(req.SessionId, $"{asset.Name} ({protocol.ToUpperInvariant()})", protocol, _rdp, _rdpCred, req));
            return;
        }

        var provider = protocol == RemoteProtocol.Ssh ? _ssh : protocol == RemoteProtocol.Telnet ? _telnet : null;
        if (provider != null && ep.CredentialRefId != null)
        {
            var req = new SessionRequest { SessionId = Guid.NewGuid().ToString("n"), Protocol = protocol, EndpointId = ep.Id, CredentialRefId = ep.CredentialRefId };
            _tabs.OpenTerminalTab(new TerminalTabViewModel(req.SessionId, $"{asset.Name} ({protocol.ToUpperInvariant()})", protocol, provider, req));
        }
        else
        {
            _tabs.OpenTab(asset.Name, protocol);
        }
    }

    private async Task LaunchWinBoxAsync(Asset asset, Endpoint ep)
    {
        if (_winBox is null)
        {
            return;
        }
        var (address, family) = ResolveAddress(ep);
        var request = new ExternalToolLaunchRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = asset.WorkspaceId,
            Tool = "winbox",
            HostId = asset.Id,
            Target = new ExternalToolTarget { Address = address, AddressFamily = family, Port = ep.Port, PreferIpv6 = ep.PreferIpv6 },
            CredentialRefId = ep.CredentialRefId,
            IncludePasswordArgument = false,
            RequestedBy = "local-user",
            RequestedAt = DateTimeOffset.UtcNow,
        };
        await _winBox.LaunchAsync(request);
    }

    private static (string address, string? family) ResolveAddress(Endpoint ep)
    {
        if (ep.PreferIpv6 && ep.Ipv6 != null) return (ep.Ipv6, "ipv6");
        if (ep.Ipv4 != null) return (ep.Ipv4, "ipv4");
        if (ep.Ipv6 != null) return (ep.Ipv6, "ipv6");
        return (ep.Fqdn ?? string.Empty, ep.Fqdn != null ? "dns" : null);
    }
}
