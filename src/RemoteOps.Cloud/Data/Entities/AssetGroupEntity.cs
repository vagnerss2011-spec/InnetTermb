namespace RemoteOps.Cloud.Data.Entities;

public sealed class AssetGroupEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ParentId { get; set; }
    public required string Name { get; set; }
    public Guid? DefaultCredentialRefId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>JSON com políticas do grupo (protocolos permitidos, aprovação obrigatória, etc.).</summary>
    public string? PolicyJson { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
    public ICollection<AssetEntity> Assets { get; set; } = [];
}
