using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Hubs;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Contracts.Sync;

namespace RemoteOps.Cloud.Sync;

public sealed class SyncService(
    AppDbContext db,
    PermissionEvaluator rbac,
    AuditService audit,
    IHubContext<SyncHub> hub,
    ILogger<SyncService> logger)
{
    private const int DefaultPageSize = 200;

    public async Task<PullResponse> PullAsync(
        Guid workspaceId,
        long cursor,
        int pageSize,
        PermissionContext permCtx,
        CancellationToken ct)
    {
        var check = await rbac.EvaluateAsync(permCtx, Permissions.SyncPull, ct);
        if (!check.Granted)
        {
            logger.LogWarning("SyncPull denied for user {UserId} workspace {WorkspaceId}: {Reason}",
                permCtx.UserId, workspaceId, check.Reason);
            throw new RbacDeniedException(check.Reason);
        }

        var limit = Math.Clamp(pageSize, 1, 1000);
        var entries = await db.Changelog
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Id > cursor)
            .OrderBy(c => c.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = entries.Count > limit;
        var page = entries.Take(limit).ToList();
        var nextCursor = page.Count > 0 ? page[^1].Id : cursor;

        var changes = page.Select(ToSyncChange).ToList();
        return new PullResponse(changes, nextCursor, hasMore);
    }

    public async Task<PushResult> PushAsync(
        Guid workspaceId,
        IReadOnlyList<SyncChange> changes,
        PermissionContext permCtx,
        CancellationToken ct)
    {
        var check = await rbac.EvaluateAsync(permCtx, Permissions.SyncPush, ct);
        if (!check.Granted)
        {
            logger.LogWarning("SyncPush denied for user {UserId} workspace {WorkspaceId}: {Reason}",
                permCtx.UserId, workspaceId, check.Reason);
            throw new RbacDeniedException(check.Reason);
        }

        var conflicts = new List<ConflictDetail>();
        long lastInsertedId = 0;

        foreach (var change in changes)
        {
            // Guard: SecretEnvelope nunca aceita merge automático
            if (string.Equals(change.EntityType, "SecretEnvelope", StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new ConflictDetail(
                    change.ClientChangeId,
                    change.EntityType,
                    change.EntityId,
                    change.BaseVersion,
                    -1,
                    "secret-envelope.no-auto-merge"));
                continue;
            }

            // Idempotência: verifica ClientChangeId já processado
            if (!string.IsNullOrEmpty(change.ClientChangeId))
            {
                var cidMarker = $"\"_cid\":\"{change.ClientChangeId}\"";
                var already = await db.Changelog
                    .AsNoTracking()
                    .AnyAsync(c =>
                        c.WorkspaceId == workspaceId &&
                        c.EntityType == change.EntityType &&
                        c.PatchJson.Contains(cidMarker), ct);
                if (already)
                {
                    logger.LogDebug("Idempotent push: ClientChangeId {Cid} already applied", change.ClientChangeId);
                    continue;
                }
            }

            // Verifica conflito de versão
            var currentVersion = await GetCurrentVersionAsync(workspaceId, change.EntityType, change.EntityId, ct);
            if (currentVersion.HasValue && change.BaseVersion < currentVersion.Value)
            {
                conflicts.Add(new ConflictDetail(
                    change.ClientChangeId,
                    change.EntityType,
                    change.EntityId,
                    change.BaseVersion,
                    currentVersion.Value,
                    "version.conflict"));
                continue;
            }

            var patch = BuildPatchJson(change);
            var entityGuid = Guid.TryParse(change.EntityId, out var parsedEid) ? parsedEid : Guid.NewGuid();
            var entry = new ChangelogEntryEntity
            {
                WorkspaceId = workspaceId,
                EntityType = change.EntityType,
                EntityId = entityGuid,
                Operation = change.Operation,
                Version = (currentVersion ?? change.BaseVersion) + 1,
                PatchJson = patch,
                ActorUserId = permCtx.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            db.Changelog.Add(entry);
            await db.SaveChangesAsync(ct);
            lastInsertedId = entry.Id;

            // Emite hint SignalR sem payload completo
            await hub.Clients.Group(workspaceId.ToString())
                .SendAsync("workspace.changed", new
                {
                    workspaceId = workspaceId.ToString(),
                    cursor = lastInsertedId,
                    entityType = change.EntityType,
                    entityId = change.EntityId,
                }, ct);

            logger.LogInformation(
                "SyncPush applied: workspace {WorkspaceId} entity {EntityType}/{EntityId} op {Op} v{Version}",
                workspaceId, change.EntityType, change.EntityId, change.Operation, entry.Version);
        }

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: workspaceId,
            ActorUserId: permCtx.UserId,
            Action: "sync.push",
            TargetType: "changelog",
            Metadata: new Dictionary<string, object?>
            {
                ["changesReceived"] = changes.Count,
                ["changesApplied"] = changes.Count - conflicts.Count,
                ["conflictsCount"] = conflicts.Count,
            }), ct);

        if (conflicts.Count > 0)
            return new PushResult("conflict", lastInsertedId > 0 ? lastInsertedId : null, conflicts);

        return new PushResult("ok", lastInsertedId > 0 ? lastInsertedId : null);
    }

    private async Task<int?> GetCurrentVersionAsync(
        Guid workspaceId, string entityType, string entityId, CancellationToken ct)
    {
        if (!Guid.TryParse(entityId, out var eid)) return null;
        var latest = await db.Changelog
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.EntityType == entityType && c.EntityId == eid)
            .OrderByDescending(c => c.Id)
            .Select(c => (int?)c.Version)
            .FirstOrDefaultAsync(ct);
        return latest;
    }

    private static string BuildPatchJson(SyncChange change)
    {
        var envelope = new Dictionary<string, object?>(change.Patch)
        {
            ["_op"] = change.Operation,
            ["_baseVersion"] = change.BaseVersion,
        };
        if (!string.IsNullOrEmpty(change.ClientChangeId))
            envelope["_cid"] = change.ClientChangeId;
        return JsonSerializer.Serialize(envelope);
    }

    private static SyncChange ToSyncChange(ChangelogEntryEntity entry)
    {
        Dictionary<string, object?> patch;
        try
        {
            patch = JsonSerializer.Deserialize<Dictionary<string, object?>>(entry.PatchJson)
                    ?? [];
        }
        catch (JsonException)
        {
            patch = [];
        }

        var clientChangeId = patch.TryGetValue("_cid", out var cid) ? cid?.ToString() : null;
        patch.Remove("_op");
        patch.Remove("_baseVersion");
        patch.Remove("_cid");

        return new SyncChange
        {
            ClientChangeId = clientChangeId,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId.ToString(),
            Operation = entry.Operation,
            BaseVersion = entry.Version - 1,
            Patch = patch,
        };
    }
}

public sealed class RbacDeniedException(string reason) : Exception(reason);
