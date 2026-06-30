namespace RemoteOps.Cloud.Data.Entities;

public sealed class ChangelogEntryEntity
{
    /// <summary>Cursor monotônico global por workspace. Auto-incremento no PostgreSQL.</summary>
    public long Id { get; set; }

    public Guid WorkspaceId { get; set; }
    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }

    /// <summary>created | updated | deleted</summary>
    public required string Operation { get; set; }

    public int Version { get; set; }

    /// <summary>Patch JSON serializado. Nunca contém plaintext de segredo.</summary>
    public required string PatchJson { get; set; }

    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
}
