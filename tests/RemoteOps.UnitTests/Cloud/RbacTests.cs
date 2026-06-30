using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class RbacTests
{
    // ── Usuário ativo ────────────────────────────────────────────────────────

    [Fact]
    public async Task Rbac_Grants_ActiveUserWithOwnerRole()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.AssetRead);

        Assert.True(result.Granted);
        Assert.Equal("granted", result.Reason);
    }

    [Fact]
    public async Task Rbac_Denies_InactiveUser()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");
        user.Status = "disabled";
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.AssetRead);

        Assert.False(result.Granted);
        Assert.Equal("user.inactive", result.Reason);
    }

    // ── Role ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rbac_Denies_WhenRoleDoesNotGrantPermission()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("ReadOnly");

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.AssetCreate);

        Assert.False(result.Granted);
        Assert.Equal("role.not-granted", result.Reason);
    }

    [Fact]
    public async Task Rbac_Grants_OperatorCanPullSync()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.SyncPull);

        Assert.True(result.Granted);
    }

    [Fact]
    public async Task Rbac_Denies_OperatorCannotPush()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("ReadOnly");

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.SyncPush);

        Assert.False(result.Granted);
    }

    // ── Negação explícita no membro vence role ────────────────────────────────

    [Fact]
    public async Task Rbac_ExplicitDenyInMembership_WinsOverRoleGrant()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, membership) = await ctx.SeedActiveUserAsync("Owner");

        // Owner tem asset.read, mas membership tem deny explícito
        membership.PermissionsJson = "{\"deny\":[\"asset.read\"]}";
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.AssetRead);

        Assert.False(result.Granted);
        Assert.Equal("member.explicit-deny", result.Reason);
    }

    [Fact]
    public async Task Rbac_ExplicitGrantInMembership_OverridesRole()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, membership) = await ctx.SeedActiveUserAsync("ReadOnly");

        // ReadOnly não tem asset.create, mas membership tem grant explícito
        membership.PermissionsJson = "{\"grant\":[\"asset.create\"]}";
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.AssetCreate);

        Assert.True(result.Granted);
    }

    // ── Negação explícita no grupo ────────────────────────────────────────────

    [Fact]
    public async Task Rbac_GroupExplicitDeny_WinsOverRoleGrant()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");

        var group = new AssetGroupEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            Name = "Restricted Group",
            PolicyJson = "{\"deny\":[\"session.ssh.open\"]}",
            Version = 1,
        };
        ctx.Db.AssetGroups.Add(group);
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id, AssetGroupId: group.Id),
            Permissions.SessionSshOpen);

        Assert.False(result.Granted);
        Assert.Equal("group.explicit-deny", result.Reason);
    }

    // ── Device revogado ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rbac_Denies_RevokedDevice()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");

        var device = new DeviceEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "Old Laptop",
            Status = "revoked",
            RegisteredAt = DateTimeOffset.UtcNow.AddDays(-30),
            RevokedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Devices.Add(device);
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id, DeviceId: device.Id),
            Permissions.AssetRead);

        Assert.False(result.Granted);
        Assert.Equal("device.revoked", result.Reason);
    }

    // ── Workspace inativo ─────────────────────────────────────────────────────

    [Fact]
    public async Task Rbac_Denies_InactiveWorkspace()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync();
        ws.Status = "suspended";
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id),
            Permissions.AssetRead);

        Assert.False(result.Granted);
        Assert.Equal("workspace.inactive", result.Reason);
    }

    // ── Cross-workspace ───────────────────────────────────────────────────────

    [Fact]
    public async Task Rbac_Denies_CrossWorkspace_WhenTenantMismatch()
    {
        using var ctx = new CloudTestContext();
        var (tenant, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");

        // Outro tenant
        var otherTenantId = Guid.NewGuid();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(user.Id, ws.Id, TenantId: otherTenantId),
            Permissions.AssetRead);

        Assert.False(result.Granted);
        Assert.Equal("workspace.cross-tenant", result.Reason);
    }

    // ── Papéis sem membership ─────────────────────────────────────────────────

    [Fact]
    public async Task Rbac_Denies_UserWithoutMembership()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, _, _) = await ctx.SeedActiveUserAsync();

        // Segundo usuário sem membership
        var stranger = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = "stranger@test.local",
            DisplayName = "Stranger",
            Status = "active",
            PasswordHash = "v1:x:x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Users.Add(stranger);
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Rbac.EvaluateAsync(
            new PermissionContext(stranger.Id, ws.Id),
            Permissions.AssetRead);

        Assert.False(result.Granted);
        Assert.Equal("membership.missing", result.Reason);
    }
}
