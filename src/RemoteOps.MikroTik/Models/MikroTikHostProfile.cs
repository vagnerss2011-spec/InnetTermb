namespace RemoteOps.MikroTik.Models;

public sealed record MikroTikHostProfile(
    string Id,
    string Name,
    string? TenantId,
    string? GroupId,
    string? IPv4,
    string? IPv6,
    int WinBoxPort = WinBoxTarget.DefaultPort,
    string? DefaultLogin = null,
    string? CredentialRefId = null,
    string? WinBoxWorkspace = null,
    bool PreferIpv6 = false,
    RoMonOptions? RoMon = null,
    bool AllowPasswordArgument = false,
    string? Notes = null
);
