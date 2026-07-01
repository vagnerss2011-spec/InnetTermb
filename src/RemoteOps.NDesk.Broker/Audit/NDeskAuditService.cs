using System.Text.Json;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Data.Entities;

namespace RemoteOps.NDesk.Broker.Audit;

public sealed record NDeskAuditRecord(
    string Action,
    Guid? WorkspaceId = null,
    Guid? TicketId = null,
    Guid? SessionId = null,
    Guid? ActorUserId = null,
    string? ActorDisplayName = null,
    Dictionary<string, object?>? Metadata = null);

/// <summary>
/// Persiste evento de auditoria NDesk (docs/09 §Auditoria). Metadata NUNCA deve conter
/// segredo, token ou conteúdo de tela — responsabilidade do caller, reforçada aqui por
/// uma allowlist de chaves bloqueadas.
/// </summary>
public sealed class NDeskAuditService(NDeskDbContext db, TimeProvider clock, ILogger<NDeskAuditService> logger)
{
    private static readonly string[] BlockedKeys =
        ["password", "secret", "token", "key", "credential", "plaintext", "hash", "linktoken"];

    public async Task RecordAsync(NDeskAuditRecord record, CancellationToken ct = default)
    {
        var safeMetadata = SanitizeMetadata(record.Metadata);
        var metadataJson = JsonSerializer.Serialize(safeMetadata);

        var entity = new NDeskAuditEventEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = record.WorkspaceId,
            TicketId = record.TicketId,
            SessionId = record.SessionId,
            ActorUserId = record.ActorUserId,
            ActorDisplayName = record.ActorDisplayName,
            Action = record.Action,
            MetadataJson = metadataJson,
            CreatedAt = clock.GetUtcNow(),
        };

        db.AuditEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "NDeskAudit: action={Action} ticket={TicketId} session={SessionId} actor={ActorUserId}",
            record.Action, record.TicketId, record.SessionId, record.ActorUserId);
    }

    private static Dictionary<string, object?> SanitizeMetadata(Dictionary<string, object?>? meta)
    {
        if (meta is null) return [];
        var safe = new Dictionary<string, object?>(meta.Count);
        foreach (var (k, v) in meta)
        {
            safe[k] = Array.Exists(BlockedKeys, b => k.Contains(b, StringComparison.OrdinalIgnoreCase))
                ? "[REDACTED]"
                : v;
        }
        return safe;
    }
}
