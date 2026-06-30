namespace RemoteOps.Contracts.Audit;

public sealed class AuditEvent
{
    public required string Id { get; init; }

    public required string WorkspaceId { get; init; }

    public required string ActorUserId { get; init; }

    public required string Action { get; init; }

    public string? TargetType { get; init; }

    public string? TargetId { get; init; }

    public string? IpAddress { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Metadados do evento. Nunca incluir segredos.</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }
}
