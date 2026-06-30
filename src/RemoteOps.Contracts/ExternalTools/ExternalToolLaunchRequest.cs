namespace RemoteOps.Contracts.ExternalTools;

public sealed class ExternalToolLaunchRequest
{
    public required string Id { get; init; }

    public required string WorkspaceId { get; init; }

    /// <summary>winbox.</summary>
    public required string Tool { get; init; }

    public string? HostId { get; init; }

    public required ExternalToolTarget Target { get; init; }

    public string? Login { get; init; }

    public string? CredentialRefId { get; init; }

    public bool IncludePasswordArgument { get; init; }

    public string? WorkspaceName { get; init; }

    public ExternalToolRomon? Romon { get; init; }

    public required string RequestedBy { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public string? PolicyDecisionId { get; init; }
}

public sealed class ExternalToolTarget
{
    public required string Address { get; init; }

    /// <summary>ipv4 | ipv6 | dns.</summary>
    public string? AddressFamily { get; init; }

    public int Port { get; init; }

    public bool PreferIpv6 { get; init; }
}

public sealed class ExternalToolRomon
{
    public bool Enabled { get; init; }

    public string? Agent { get; init; }

    public string? ConnectTo { get; init; }
}
