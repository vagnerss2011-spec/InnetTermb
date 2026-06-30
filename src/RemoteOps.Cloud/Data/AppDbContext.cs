using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data.Entities;

namespace RemoteOps.Cloud.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();
    public DbSet<AssetGroupEntity> AssetGroups => Set<AssetGroupEntity>();
    public DbSet<AssetEntity> Assets => Set<AssetEntity>();
    public DbSet<EndpointEntity> Endpoints => Set<EndpointEntity>();
    public DbSet<CredentialRefEntity> CredentialRefs => Set<CredentialRefEntity>();
    public DbSet<SecretEnvelopeEntity> SecretEnvelopes => Set<SecretEnvelopeEntity>();
    public DbSet<ChangelogEntryEntity> Changelog => Set<ChangelogEntryEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
        });

        model.Entity<WorkspaceEntity>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.TenantId);
            e.HasOne(x => x.Tenant).WithMany(x => x.Workspaces).HasForeignKey(x => x.TenantId);
        });

        model.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
        });

        model.Entity<MembershipEntity>(e =>
        {
            e.ToTable("memberships");
            e.HasKey(x => new { x.WorkspaceId, x.UserId });
            e.Property(x => x.Role).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.WorkspaceId);
            e.HasOne(x => x.Workspace).WithMany(x => x.Memberships).HasForeignKey(x => x.WorkspaceId);
            e.HasOne(x => x.User).WithMany(x => x.Memberships).HasForeignKey(x => x.UserId);
        });

        model.Entity<AssetGroupEntity>(e =>
        {
            e.ToTable("asset_groups");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasOne(x => x.Workspace).WithMany(x => x.AssetGroups).HasForeignKey(x => x.WorkspaceId);
        });

        model.Entity<AssetEntity>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.GroupId);
            e.HasOne(x => x.Group).WithMany(x => x.Assets).HasForeignKey(x => x.GroupId);
        });

        model.Entity<EndpointEntity>(e =>
        {
            e.ToTable("endpoints");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AssetId);
            e.Property(x => x.Protocol).HasMaxLength(50).IsRequired();
            e.HasOne(x => x.Asset).WithMany(x => x.Endpoints).HasForeignKey(x => x.AssetId);
        });

        model.Entity<CredentialRefEntity>(e =>
        {
            e.ToTable("credential_refs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(x => x.CredentialRefs).HasForeignKey(x => x.WorkspaceId);
            e.HasOne(x => x.SecretEnvelope).WithMany().HasForeignKey(x => x.SecretEnvelopeId).IsRequired(false);
        });

        model.Entity<SecretEnvelopeEntity>(e =>
        {
            e.ToTable("secret_envelopes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Algorithm).HasMaxLength(100).IsRequired();
            e.Property(x => x.KeyVersion).HasMaxLength(100).IsRequired();
        });

        model.Entity<ChangelogEntryEntity>(e =>
        {
            e.ToTable("changelog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.EntityId);
            e.HasIndex(x => new { x.WorkspaceId, x.Id });
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Operation).HasMaxLength(20).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(x => x.Changelog).HasForeignKey(x => x.WorkspaceId);
        });

        model.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.Property(x => x.Action).HasMaxLength(200).IsRequired();
            e.Property(x => x.MetadataJson).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(x => x.AuditEvents).HasForeignKey(x => x.WorkspaceId);
        });

        model.Entity<DeviceEntity>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.Devices).HasForeignKey(x => x.UserId);
        });

        model.Entity<RefreshTokenEntity>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.TokenHash).HasMaxLength(256).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId);
        });
    }
}
