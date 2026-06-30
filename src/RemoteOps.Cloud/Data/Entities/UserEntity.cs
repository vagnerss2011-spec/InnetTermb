namespace RemoteOps.Cloud.Data.Entities;

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public required string Status { get; set; }
    public bool MfaRequired { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<MembershipEntity> Memberships { get; set; } = [];
    public ICollection<DeviceEntity> Devices { get; set; } = [];
    public ICollection<RefreshTokenEntity> RefreshTokens { get; set; } = [];
}
