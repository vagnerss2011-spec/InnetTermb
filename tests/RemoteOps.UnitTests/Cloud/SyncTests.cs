using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;
using RemoteOps.Contracts.Sync;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class SyncTests
{
    // ── Pull com cursor ───────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_ReturnsPaginatedChanges_AfterCursor()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        // Seed 5 entradas de changelog manualmente
        for (var i = 1; i <= 5; i++)
        {
            ctx.Db.Changelog.Add(new ChangelogEntryEntity
            {
                WorkspaceId = ws.Id,
                EntityType = "Asset",
                EntityId = Guid.NewGuid(),
                Operation = "created",
                Version = i,
                PatchJson = $"{{\"name\":\"asset-{i}\"}}",
                ActorUserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await ctx.Db.SaveChangesAsync();

        // Pull desde cursor 0 com pageSize 3
        var result = await ctx.Sync.PullAsync(ws.Id, 0, 3, permCtx, default);

        Assert.Equal(3, result.Changes.Count);
        Assert.True(result.HasMore);
        Assert.True(result.NextCursor > 0);

        // Pull continuação com o novo cursor
        var result2 = await ctx.Sync.PullAsync(ws.Id, result.NextCursor, 3, permCtx, default);
        Assert.Equal(2, result2.Changes.Count);
        Assert.False(result2.HasMore);
    }

    [Fact]
    public async Task Pull_ReturnsEmpty_WhenNothingAfterCursor()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var result = await ctx.Sync.PullAsync(ws.Id, 99999, 200, permCtx, default);

        Assert.Empty(result.Changes);
        Assert.False(result.HasMore);
        Assert.Equal(99999, result.NextCursor);
    }

    // ── Pull - RBAC negado ────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_Throws_WhenRbacDenied()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("ReadOnly");

        // ReadOnly tem SyncPull
        var permCtx = new PermissionContext(user.Id, ws.Id);
        // Não deve lançar
        var result = await ctx.Sync.PullAsync(ws.Id, 0, 10, permCtx, default);
        Assert.NotNull(result);

        // Usuário sem membership não deve conseguir pull
        var stranger = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "stranger2@test.local",
            DisplayName = "Stranger2",
            Status = "active",
            PasswordHash = "v1:x:x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Users.Add(stranger);
        await ctx.Db.SaveChangesAsync();

        var strangerCtx = new PermissionContext(stranger.Id, ws.Id);
        await Assert.ThrowsAsync<RbacDeniedException>(() =>
            ctx.Sync.PullAsync(ws.Id, 0, 10, strangerCtx, default));
    }

    // ── Push sem conflito ─────────────────────────────────────────────────────

    [Fact]
    public async Task Push_Applies_WhenBaseVersionMatches()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Manager");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var entityId = Guid.NewGuid().ToString();
        var changes = new List<SyncChange>
        {
            new()
            {
                ClientChangeId = Guid.NewGuid().ToString(),
                EntityType = "Asset",
                EntityId = entityId,
                Operation = "created",
                BaseVersion = 0,
                Patch = new Dictionary<string, object?> { ["name"] = "Router-01" },
            },
        };

        var result = await ctx.Sync.PushAsync(ws.Id, changes, permCtx, default);

        Assert.Equal("ok", result.Status);
        Assert.Null(result.Conflicts);
        Assert.True(result.NewCursor > 0);

        // Confirma que entrou no changelog
        var entry = ctx.Db.Changelog.FirstOrDefault(c => c.EntityId.ToString() == entityId);
        Assert.NotNull(entry);
        Assert.Equal("created", entry.Operation);
        Assert.Equal(1, entry.Version);
    }

    // ── Push com conflito de versão ───────────────────────────────────────────

    [Fact]
    public async Task Push_ReturnsConflict_WhenBaseVersionIsStale()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Manager");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var entityId = Guid.NewGuid();

        // Aplica versão 1 primeiro
        ctx.Db.Changelog.Add(new ChangelogEntryEntity
        {
            WorkspaceId = ws.Id,
            EntityType = "Asset",
            EntityId = entityId,
            Operation = "created",
            Version = 1,
            PatchJson = "{\"name\":\"Router-01\"}",
            ActorUserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        // Tenta push com BaseVersion = 0 (stale — servidor já está na v1)
        var changes = new List<SyncChange>
        {
            new()
            {
                ClientChangeId = "conflict-test",
                EntityType = "Asset",
                EntityId = entityId.ToString(),
                Operation = "updated",
                BaseVersion = 0,
                Patch = new Dictionary<string, object?> { ["name"] = "Router-CONFLITO" },
            },
        };

        var result = await ctx.Sync.PushAsync(ws.Id, changes, permCtx, default);

        Assert.Equal("conflict", result.Status);
        Assert.NotNull(result.Conflicts);
        Assert.Single(result.Conflicts);
        Assert.Equal("version.conflict", result.Conflicts[0].Reason);
        Assert.Equal(0, result.Conflicts[0].BaseVersion);
        Assert.Equal(1, result.Conflicts[0].CurrentVersion);
    }

    // ── Push idempotente por ClientChangeId ───────────────────────────────────

    [Fact]
    public async Task Push_Idempotent_WhenSameClientChangeIdSentTwice()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Manager");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var clientChangeId = Guid.NewGuid().ToString();
        var changes = new List<SyncChange>
        {
            new()
            {
                ClientChangeId = clientChangeId,
                EntityType = "Asset",
                EntityId = Guid.NewGuid().ToString(),
                Operation = "created",
                BaseVersion = 0,
                Patch = new Dictionary<string, object?> { ["name"] = "Idempotent-Asset" },
            },
        };

        // Primeira aplicação
        var r1 = await ctx.Sync.PushAsync(ws.Id, changes, permCtx, default);
        Assert.Equal("ok", r1.Status);

        // Segunda aplicação com mesmo ClientChangeId — não deve duplicar
        var r2 = await ctx.Sync.PushAsync(ws.Id, changes, permCtx, default);
        Assert.Equal("ok", r2.Status);

        var count = ctx.Db.Changelog.Count(c => c.WorkspaceId == ws.Id &&
            c.PatchJson.Contains(clientChangeId));
        Assert.Equal(1, count);
    }

    // ── SecretEnvelope nunca aceita merge automático ──────────────────────────

    [Fact]
    public async Task Push_AlwaysConflict_ForSecretEnvelope()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var changes = new List<SyncChange>
        {
            new()
            {
                EntityType = "SecretEnvelope",
                EntityId = Guid.NewGuid().ToString(),
                Operation = "updated",
                BaseVersion = 0,
                Patch = new Dictionary<string, object?> { ["ciphertext"] = "FAKE_BLOB" },
            },
        };

        var result = await ctx.Sync.PushAsync(ws.Id, changes, permCtx, default);

        Assert.Equal("conflict", result.Status);
        Assert.NotNull(result.Conflicts);
        Assert.Equal("secret-envelope.no-auto-merge", result.Conflicts[0].Reason);
    }
}
