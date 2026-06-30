namespace RemoteOps.Rdp;

/// <summary>
/// Política de redirecionamentos do MSTSCAX. Todos OFF por padrão (requisito de
/// segurança) — só habilitados via política/profile explícito.
/// </summary>
public sealed record RdpRedirectionPolicy
{
    public bool ClipboardRedirectionEnabled { get; init; }
    public bool DriveRedirectionEnabled { get; init; }
    public bool PrinterRedirectionEnabled { get; init; }
    public bool AudioRedirectionEnabled { get; init; }
    public bool UsbRedirectionEnabled { get; init; }

    public static RdpRedirectionPolicy Default { get; } = new();
}
