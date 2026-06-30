namespace RemoteOps.Cloud.Data.Entities;

public sealed class AssetEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid GroupId { get; set; }
    public required string Name { get; set; }
    public string? Vendor { get; set; }
    public string? Model { get; set; }
    public string? Site { get; set; }
    public string? TagsJson { get; set; }

    /// <summary>Notas cifradas pelo cliente — servidor guarda opaque blob.</summary>
    public string? NotesEncrypted { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public AssetGroupEntity Group { get; set; } = null!;
    public ICollection<EndpointEntity> Endpoints { get; set; } = [];
}
