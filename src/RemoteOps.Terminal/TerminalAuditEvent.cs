namespace RemoteOps.Terminal;

/// <summary>
/// Evento de auditoria de sessão terminal. Por construção NÃO contém credencial, senha
/// ou conteúdo de terminal — apenas identificadores e metadados inócuos.
/// </summary>
public sealed record TerminalAuditEvent
{
    public required string Action { get; init; }
    public required string SessionId { get; init; }
    public required string Host { get; init; }
    public required string Protocol { get; init; }

    /// <summary>Fingerprint SHA-256 da host key (hex). Não é um segredo.</summary>
    public string? Fingerprint { get; init; }

    public string? UserId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    public override string ToString() => $"{Action} {Protocol}://{Host} [{SessionId}]";
}
