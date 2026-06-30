using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Audit;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class AuditTests
{
    // ── Grava evento ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Audit_Records_Event()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync();

        await ctx.Audit.RecordAsync(new AuditRecord(
            WorkspaceId: ws.Id,
            ActorUserId: user.Id,
            Action: "sync.push",
            Metadata: new Dictionary<string, object?> { ["changesApplied"] = 3 }));

        var events = await ctx.Db.AuditEvents.ToListAsync();
        Assert.Single(events);
        Assert.Equal("sync.push", events[0].Action);
        Assert.Equal(ws.Id, events[0].WorkspaceId);
        Assert.Equal(user.Id, events[0].ActorUserId);
    }

    // ── Metadata não contém segredo ───────────────────────────────────────────

    [Fact]
    public async Task Audit_Sanitizes_SecretKeywords_InMetadata()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync();

        await ctx.Audit.RecordAsync(new AuditRecord(
            WorkspaceId: ws.Id,
            ActorUserId: user.Id,
            Action: "credential.rotate",
            Metadata: new Dictionary<string, object?>
            {
                ["credentialId"] = "abc-123",
                ["password"] = "super-secret-value",
                ["tokenHash"] = "sha256-of-something",
                ["displayName"] = "Router Admin",
            }));

        var events = await ctx.Db.AuditEvents.ToListAsync();
        var json = events[0].MetadataJson;

        Assert.Contains("credentialId", json);
        Assert.Contains("displayName", json);
        Assert.DoesNotContain("super-secret-value", json);
        Assert.Contains("[REDACTED]", json);
    }

    [Fact]
    public async Task Audit_NullMetadata_DoesNotThrow()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync();

        await ctx.Audit.RecordAsync(new AuditRecord(
            WorkspaceId: ws.Id,
            ActorUserId: user.Id,
            Action: "auth.login"));

        var events = await ctx.Db.AuditEvents.ToListAsync();
        Assert.Single(events);
        Assert.Equal("{}", events[0].MetadataJson);
    }

    // ── ToContractEvent mapeia corretamente ───────────────────────────────────

    [Fact]
    public async Task Audit_ToContractEvent_MapsFields()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync();

        var targetId = Guid.NewGuid();
        await ctx.Audit.RecordAsync(new AuditRecord(
            WorkspaceId: ws.Id,
            ActorUserId: user.Id,
            Action: "asset.delete",
            TargetType: "Asset",
            TargetId: targetId));

        var entity = await ctx.Db.AuditEvents.FirstAsync();
        var contract = ctx.Audit.ToContractEvent(entity);

        Assert.Equal("asset.delete", contract.Action);
        Assert.Equal("Asset", contract.TargetType);
        Assert.Equal(targetId.ToString(), contract.TargetId);
        Assert.DoesNotContain("secret", contract.Metadata.Keys, StringComparer.OrdinalIgnoreCase);
    }
}
