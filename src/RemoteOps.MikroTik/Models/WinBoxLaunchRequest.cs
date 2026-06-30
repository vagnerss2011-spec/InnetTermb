using System.Net;
using System.Net.Sockets;

namespace RemoteOps.MikroTik.Models;

public sealed record WinBoxLaunchRequest(
    string Id,
    string WorkspaceId,
    string HostId,
    WinBoxTarget Target,
    string Login,
    string? CredentialRefId,
    bool IncludePasswordArgument,
    string? WorkspaceName,
    RoMonOptions? RoMon,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? PolicyDecisionId = null
);

public sealed record WinBoxTarget(
    string Address,
    WinBoxAddressFamily AddressFamily,
    int Port,
    bool PreferIpv6 = false
)
{
    public const int DefaultPort = 8291;

    public bool IsIPv6Literal =>
        IPAddress.TryParse(Address, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetworkV6;
}

public enum WinBoxAddressFamily { IPv4, IPv6, Dns }

public sealed record RoMonOptions(
    bool Enabled,
    string? Agent,
    string? ConnectTo
);
