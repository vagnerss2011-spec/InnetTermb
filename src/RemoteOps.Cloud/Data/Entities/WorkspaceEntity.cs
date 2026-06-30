namespace RemoteOps.Cloud.Data.Entities;

public sealed class WorkspaceEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? EncryptionPolicy { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public TenantEntity Tenant { get; set; } = null!;
    public ICollection<MembershipEntity> Memberships { get; set; } = [];
    public ICollection<AssetGroupEntity> AssetGroups { get; set; } = [];
    public ICollection<CredentialRefEntity> CredentialRefs { get; set; } = [];
    public ICollection<ChangelogEntryEntity> Changelog { get; set; } = [];
    public ICollection<AuditEventEntity> AuditEvents { get; set; } = [];
}
