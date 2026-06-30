namespace RemoteOps.Cloud.Data.Entities;

public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }

    /// <summary>active | revoked</summary>
    public required string Status { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public UserEntity User { get; set; } = null!;
}
