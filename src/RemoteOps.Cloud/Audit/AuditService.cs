using System.Text.Json;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Contracts.Audit;

namespace RemoteOps.Cloud.Audit;

public sealed record AuditRecord(
    Guid WorkspaceId,
    Guid ActorUserId,
    string Action,
    string? TargetType = null,
    Guid? TargetId = null,
    string? IpAddress = null,
    Guid? DeviceId = null,
    Dictionary<string, object?>? Metadata = null);

/// <summary>
/// Persiste AuditEvent para toda ação sensível.
/// Metadata NUNCA deve conter segredo — responsabilidade do caller.
/// </summary>
public sealed class AuditService(AppDbContext db, ILogger<AuditService> logger)
{
    public async Task RecordAsync(AuditRecord record, CancellationToken ct = default)
    {
        var safeMetadata = SanitizeMetadata(record.Metadata);
        var metadataJson = JsonSerializer.Serialize(safeMetadata);

        var entity = new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = record.WorkspaceId,
            ActorUserId = record.ActorUserId,
            Action = record.Action,
            TargetType = record.TargetType,
            TargetId = record.TargetId,
            IpAddress = record.IpAddress,
            DeviceId = record.DeviceId,
            MetadataJson = metadataJson,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.AuditEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Audit: action={Action} workspace={WorkspaceId} actor={ActorUserId} target={TargetType}/{TargetId}",
            record.Action, record.WorkspaceId, record.ActorUserId,
            record.TargetType, record.TargetId);
    }

    /// <summary>Converte para o tipo canônico do contrato (RemoteOps.Contracts.Audit).</summary>
    public AuditEvent ToContractEvent(AuditEventEntity entity)
    {
        Dictionary<string, object?> metadata;
        try { metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.MetadataJson) ?? []; }
        catch (JsonException) { metadata = []; }

        return new AuditEvent
        {
            Id = entity.Id.ToString(),
            WorkspaceId = entity.WorkspaceId.ToString(),
            ActorUserId = entity.ActorUserId.ToString(),
            Action = entity.Action,
            TargetType = entity.TargetType,
            TargetId = entity.TargetId?.ToString(),
            IpAddress = entity.IpAddress,
            DeviceId = entity.DeviceId?.ToString(),
            Metadata = metadata,
            CreatedAt = entity.CreatedAt,
        };
    }

    private static Dictionary<string, object?> SanitizeMetadata(Dictionary<string, object?>? meta)
    {
        if (meta is null) return [];
        // Bloqueia chaves que poderiam conter segredos mesmo que o caller cometa o erro
        string[] blockedKeys = ["password", "secret", "token", "key", "credential", "plaintext", "hash"];
        var safe = new Dictionary<string, object?>(meta.Count);
        foreach (var (k, v) in meta)
        {
            if (Array.Exists(blockedKeys, b => k.Contains(b, StringComparison.OrdinalIgnoreCase)))
                safe[k] = "[REDACTED]";
            else
                safe[k] = v;
        }
        return safe;
    }
}
