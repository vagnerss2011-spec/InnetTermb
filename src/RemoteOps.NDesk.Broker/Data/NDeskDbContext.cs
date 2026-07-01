using Microsoft.EntityFrameworkCore;
using RemoteOps.NDesk.Broker.Data.Entities;

namespace RemoteOps.NDesk.Broker.Data;

public sealed class NDeskDbContext(DbContextOptions<NDeskDbContext> options) : DbContext(options)
{
    public DbSet<NDeskTicketEntity> Tickets => Set<NDeskTicketEntity>();
    public DbSet<NDeskPermissionGrantEntity> PermissionGrants => Set<NDeskPermissionGrantEntity>();
    public DbSet<NDeskSessionTelemetryEntity> Telemetry => Set<NDeskSessionTelemetryEntity>();
    public DbSet<NDeskAuditEventEntity> AuditEvents => Set<NDeskAuditEventEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<NDeskTicketEntity>(e =>
        {
            e.ToTable("ndesk_tickets");
            e.HasKey(x => x.Id);
            e.Property(x => x.LinkTokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.LinkTokenHash).IsUnique();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.SessionId);
        });

        model.Entity<NDeskPermissionGrantEntity>(e =>
        {
            e.ToTable("ndesk_permission_grants");
            e.HasKey(x => x.SessionId);
            e.Property(x => x.Mode).HasMaxLength(20).IsRequired();
            e.Property(x => x.GrantedByDisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.GrantedByMachineName).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.TicketId);
        });

        model.Entity<NDeskSessionTelemetryEntity>(e =>
        {
            e.ToTable("ndesk_session_telemetry");
            e.HasKey(x => x.Id);
            e.Property(x => x.Route).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => new { x.SessionId, x.Timestamp });
        });

        model.Entity<NDeskAuditEventEntity>(e =>
        {
            e.ToTable("ndesk_audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.MetadataJson).IsRequired();
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.TicketId);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        });
    }
}
