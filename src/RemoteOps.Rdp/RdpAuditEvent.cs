namespace RemoteOps.Rdp;

/// <summary>
/// Evento de auditoria de sessão RDP. Por construção NÃO contém credencial, senha
/// ou conteúdo de tela — apenas identificadores e metadados inócuos.
/// </summary>
public sealed record RdpAuditEvent
{
    public required string Action { get; init; }
    public required string SessionId { get; init; }
    public required string Host { get; init; }

    /// <summary>Thumbprint SHA-256/SHA-1 do certificado do servidor (hex). Não é um segredo.</summary>
    public string? CertificateThumbprint { get; init; }

    public string? UserId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    public override string ToString() => $"{Action} rdp://{Host} [{SessionId}]";
}

public interface IRdpAuditSink
{
    Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>Ações de auditoria RDP. Alinhadas a docs/08-rdp-terminal-server.md.</summary>
public static class RdpActions
{
    public const string SessionOpened = "rdp.session.opened";
    public const string SessionClosed = "rdp.session.closed";
    public const string CertificateAccepted = "rdp.certificate.accepted";
    public const string CertificateRejected = "rdp.certificate.rejected";
    public const string ConnectFailed = "rdp.connect.failed";
}
