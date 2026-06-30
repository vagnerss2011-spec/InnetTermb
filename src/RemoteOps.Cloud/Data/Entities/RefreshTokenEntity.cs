namespace RemoteOps.Cloud.Data.Entities;

public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }

    /// <summary>Hash SHA-256 do token; nunca o valor em claro.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public UserEntity User { get; set; } = null!;
}
