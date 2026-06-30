namespace RemoteOps.Contracts.Models;

public sealed record SessionRequest
{
    public required string SessionId { get; init; }
    public required RemoteProtocol Protocol { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public bool PreferIpv6 { get; init; } = true;
    public TerminalSize Terminal { get; init; } = new(120, 32);
    public required string CredentialRefId { get; init; }
}

public sealed record TerminalSize(int Cols, int Rows);

public enum RemoteProtocol { Ssh, Telnet }
