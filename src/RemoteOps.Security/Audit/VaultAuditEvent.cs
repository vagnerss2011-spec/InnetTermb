namespace RemoteOps.Security.Audit;

/// <summary>
/// Evento de auditoria do cofre. Por construção NÃO possui nenhum campo de
/// segredo — apenas identificadores e metadados. <see cref="ToString"/> é seguro.
/// </summary>
public sealed record VaultAuditEvent
{
    public required string Action { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ActorUserId { get; init; }

    public string? EnvelopeId { get; init; }

    public string? CredentialId { get; init; }

    public int? Version { get; init; }

    public string? DeviceId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}
