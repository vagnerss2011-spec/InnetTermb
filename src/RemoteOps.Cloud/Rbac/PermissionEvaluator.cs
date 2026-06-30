using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;

namespace RemoteOps.Cloud.Rbac;

/// <summary>Resultado da avaliação de permissão — inclui motivo para auditoria.</summary>
public sealed record PermissionResult(bool Granted, string Reason);

/// <summary>
/// Avalia permissão na ordem definida em docs/18:
/// 1. Usuário ativo → 2. Device autorizado → 3. Workspace ativo →
/// 4. Role concede → 5. Grupo/asset nega explicitamente → 6. Aprovação obrigatória.
/// Negação explícita sempre vence herança.
/// </summary>
public sealed class PermissionEvaluator(AppDbContext db)
{
    public async Task<PermissionResult> EvaluateAsync(
        PermissionContext ctx,
        string permission,
        CancellationToken ct = default)
    {
        // 1. Usuário ativo
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == ctx.UserId, ct);
        if (user is null || user.Status != "active")
            return new PermissionResult(false, "user.inactive");

        // 2. Device autorizado
        if (ctx.DeviceId.HasValue)
        {
            var device = await db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == ctx.DeviceId && d.UserId == ctx.UserId, ct);
            if (device is null || device.Status == "revoked")
                return new PermissionResult(false, "device.revoked");
        }

        // 3. Workspace ativo
        var workspace = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == ctx.WorkspaceId, ct);
        if (workspace is null || workspace.Status != "active")
            return new PermissionResult(false, "workspace.inactive");

        // Verifica que workspace pertence ao tenant do usuário (cross-workspace leak prevention)
        if (ctx.TenantId.HasValue && workspace.TenantId != ctx.TenantId.Value)
            return new PermissionResult(false, "workspace.cross-tenant");

        // 4. Role concede?
        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == ctx.WorkspaceId && m.UserId == ctx.UserId, ct);
        if (membership is null)
            return new PermissionResult(false, "membership.missing");

        // Permissões granulares sobrescritas por membro têm prioridade sobre o role
        var memberGranted = MemberPermissionGrants(membership.PermissionsJson, permission);
        var memberDenied = MemberPermissionDenies(membership.PermissionsJson, permission);

        if (memberDenied)
            return new PermissionResult(false, "member.explicit-deny");

        var roleGranted = memberGranted ?? Roles.RoleGrants(membership.Role, permission);
        if (!roleGranted)
            return new PermissionResult(false, "role.not-granted");

        // 5. Grupo/asset nega explicitamente?
        if (ctx.AssetGroupId.HasValue)
        {
            var group = await db.AssetGroups.AsNoTracking()
                .FirstOrDefaultAsync(g => g.Id == ctx.AssetGroupId && g.WorkspaceId == ctx.WorkspaceId, ct);
            if (group is not null && GroupDeniesPermission(group.PolicyJson, permission))
                return new PermissionResult(false, "group.explicit-deny");
        }

        // 6. Aprovação obrigatória (apenas sinaliza — a decisão de bloquear fica no caller)
        if (ctx.AssetGroupId.HasValue && RequiresApproval(ctx, permission))
            return new PermissionResult(false, "approval.required");

        return new PermissionResult(true, "granted");
    }

    private static bool? MemberPermissionGrants(string? json, string permission)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("grant", out var grantEl) &&
                grantEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in grantEl.EnumerateArray())
                    if (item.GetString() == permission) return true;
            }
        }
        catch (JsonException) { /* ignore malformed json */ }
        return null;
    }

    private static bool MemberPermissionDenies(string? json, string permission)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("deny", out var denyEl) &&
                denyEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in denyEl.EnumerateArray())
                    if (item.GetString() == permission) return true;
            }
        }
        catch (JsonException) { /* ignore malformed json */ }
        return false;
    }

    private static bool GroupDeniesPermission(string? policyJson, string permission)
    {
        if (string.IsNullOrEmpty(policyJson)) return false;
        try
        {
            var doc = JsonDocument.Parse(policyJson);
            if (doc.RootElement.TryGetProperty("deny", out var denyEl) &&
                denyEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in denyEl.EnumerateArray())
                    if (item.GetString() == permission) return true;
            }
        }
        catch (JsonException) { /* ignore malformed json */ }
        return false;
    }

    private static bool RequiresApproval(PermissionContext ctx, string permission)
        => ctx.RequiresApprovalPermissions?.Contains(permission) is true;
}

public sealed record PermissionContext(
    Guid UserId,
    Guid WorkspaceId,
    Guid? DeviceId = null,
    Guid? TenantId = null,
    Guid? AssetGroupId = null,
    HashSet<string>? RequiresApprovalPermissions = null);
