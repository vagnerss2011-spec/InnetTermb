namespace RemoteOps.Cloud.Data.Entities;

public sealed class TenantEntity
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<WorkspaceEntity> Workspaces { get; set; } = [];
}
