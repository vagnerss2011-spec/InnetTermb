namespace RemoteOps.Contracts.Sessions;

public sealed class SessionRequest
{
    public required string SessionId { get; init; }

    /// <summary>Protocolo: ssh | telnet | rdp | mikrotik | ndesk.</summary>
    public required string Protocol { get; init; }

    public required string EndpointId { get; init; }

    public required string CredentialRefId { get; init; }

    public bool PreferIpv6 { get; init; } = true;

    public TerminalOptions? Terminal { get; init; }
}

public sealed class TerminalOptions
{
    public int Cols { get; init; } = 80;

    public int Rows { get; init; } = 24;

    public string? Encoding { get; init; }
}
