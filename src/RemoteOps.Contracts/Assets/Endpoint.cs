namespace RemoteOps.Contracts.Assets;

public sealed class Endpoint
{
    public required string Id { get; init; }

    public required string AssetId { get; init; }

    /// <summary>ssh | telnet | rdp | mikrotik | ndesk.</summary>
    public required string Protocol { get; init; }

    public string? Fqdn { get; init; }

    public string? Ipv4 { get; init; }

    public string? Ipv6 { get; init; }

    public int Port { get; init; }

    public bool PreferIpv6 { get; init; } = true;

    public string? CredentialRefId { get; init; }

    public EndpointProfile? Profile { get; init; }
}

public sealed class EndpointProfile
{
    public string? VendorProfile { get; init; }

    public string? TerminalEncoding { get; init; }

    /// <summary>Perfil de segurança SSH: "auto" (default permissivo) | "strict" (só algoritmos fortes). null = auto.</summary>
    public string? SshAlgorithmProfile { get; init; }
}
