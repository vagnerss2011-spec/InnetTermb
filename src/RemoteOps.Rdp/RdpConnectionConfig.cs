namespace RemoteOps.Rdp;

/// <summary>Configuração de conexão RDP resolvida — sem segredo, sem COM, totalmente testável.</summary>
public sealed record RdpConnectionConfig
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required bool NlaRequired { get; init; }
    public required RdpRedirectionPolicy Redirection { get; init; }
}
