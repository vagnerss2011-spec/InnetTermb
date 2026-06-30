namespace RemoteOps.Cloud.Data.Entities;

public sealed class CredentialRefEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Scope { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? CredentialGroupId { get; set; }
    public string? MetadataJson { get; set; }
    public Guid? SecretEnvelopeId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
    public SecretEnvelopeEntity? SecretEnvelope { get; set; }
}
