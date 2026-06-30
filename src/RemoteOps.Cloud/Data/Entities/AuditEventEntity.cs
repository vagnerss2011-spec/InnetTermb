namespace RemoteOps.Cloud.Data.Entities;

public sealed class AuditEventEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorUserId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? IpAddress { get; set; }
    public Guid? DeviceId { get; set; }

    /// <summary>Metadados sanitizados em JSON. NUNCA contém segredo.</summary>
    public required string MetadataJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
}
