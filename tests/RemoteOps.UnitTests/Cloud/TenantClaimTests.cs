using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RemoteOps.Cloud.Auth;
using RemoteOps.Cloud.Sync;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// FIX 3 (LOW, defense-in-depth): o access token passa a emitir a claim tenant_id e o
/// ToPermissionContext a popula — antes a guarda cross-tenant do PermissionEvaluator era
/// código morto (tenant_id nunca chegava, ficava sempre nula).
/// </summary>
public sealed class TenantClaimTests
{
    private static RegisterRequest NewRegister(string email) => new(
        Email: email,
        Argon2Salt: Convert.ToBase64String(new byte[16]),
        Argon2Params: new Argon2Params(65536, 3, 1, 32),
        AuthHash: Convert.ToBase64String(new byte[32]),
        WrappedAmkPwd: Convert.ToBase64String(new byte[60]),
        WrappedAmkRec: Convert.ToBase64String(new byte[60]),
        AmkKeyVersion: 1,
        DeviceId: Guid.NewGuid().ToString(),
        DeviceName: "Device A",
        WorkspaceName: "Meu Workspace");

    // ── O token carrega tenant_id = tenant do usuário ─────────────────────────

    [Fact]
    public async Task AccessToken_CarriesTenantIdClaim_MatchingUserTenant()
    {
        using var ctx = new CloudTestContext();
        var reg = await ctx.Accounts.RegisterAsync(NewRegister("dono@test.local"), "1.2.3.4", default);
        Assert.NotNull(reg);

        var expectedTenant = ctx.Db.Workspaces.Single().TenantId;

        var payload = DecodeJwtPayload(reg!.AccessToken);
        Assert.True(payload.TryGetValue("tenant_id", out var tid), "token deveria conter a claim tenant_id");
        Assert.Equal(expectedTenant.ToString(), tid.GetString());
    }

    // ── ToPermissionContext lê a claim tenant_id ──────────────────────────────

    [Fact]
    public void ToPermissionContext_PopulatesTenantId_FromClaim()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var wsId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var httpCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", userId.ToString()),
                new Claim("tenant_id", tenantId.ToString()),
            ], "test")),
        };

        var permCtx = httpCtx.ToPermissionContext(wsId, deviceId);

        Assert.Equal(userId, permCtx.UserId);
        Assert.Equal(wsId, permCtx.WorkspaceId);
        Assert.Equal(deviceId, permCtx.DeviceId);
        Assert.Equal(tenantId, permCtx.TenantId);
    }

    [Fact]
    public void ToPermissionContext_LeavesTenantIdNull_WhenClaimAbsent()
    {
        // Compat: token sem a claim (ex.: emitido antes do fix) não deve NEGAR acesso —
        // a guarda cross-tenant fica inerte, e o membership continua protegendo.
        var httpCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", Guid.NewGuid().ToString())], "test")),
        };

        var permCtx = httpCtx.ToPermissionContext(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(permCtx.TenantId);
    }

    /// <summary>Decodifica o payload do JWT sem dependência externa (base64url → JSON).</summary>
    private static Dictionary<string, JsonElement> DecodeJwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }
}
