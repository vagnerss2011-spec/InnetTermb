namespace RemoteOps.Cloud.Data.Entities;

public sealed class MembershipEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public required string Role { get; set; }

    /// <summary>JSON com permissões granulares sobrescritas para este membro.</summary>
    public string? PermissionsJson { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
    public UserEntity User { get; set; } = null!;
}
