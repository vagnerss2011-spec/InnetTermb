using RemoteOps.Cloud.Auth;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Fluxo 2FA no backend (spec Fase 3): enroll (gera, NÃO ativa) → confirm (ativa) → login exige TOTP;
/// disable exige código válido. Segue o padrão AppDbContext InMemory dos AuthE2eeTests (ver a
/// LIMITAÇÃO CONHECIDA lá: sem Testcontainers/Postgres).
/// </summary>
public sealed class MfaServiceTests
{
    // Relógio fixo → código TOTP determinístico nos testes (o mesmo que MfaService/TokensAtFixedNow usam).
    private static DateTimeOffset FixedNow => CloudTestContext.FixedNow;

    private static async Task<(CloudTestContext Ctx, Guid UserId, string Email)> SeedE2eeUserAsync(
        CloudTestContext ctx)
    {
        var email = "operador@test.local";
        await ctx.Accounts.RegisterAsync(
            new RegisterRequest(
                Email: email,
                Argon2Salt: Convert.ToBase64String(new byte[16]),
                Argon2Params: new Argon2Params(65536, 3, 1, 32),
                AuthHash: Convert.ToBase64String(AuthHash),
                WrappedAmkPwd: Convert.ToBase64String(new byte[60]),
                WrappedAmkRec: Convert.ToBase64String(new byte[60]),
                AmkKeyVersion: 1,
                DeviceId: Guid.NewGuid().ToString(),
                DeviceName: "Device A",
                WorkspaceName: "WS"),
            "1.2.3.4", default);
        var user = ctx.Db.Users.Single();
        return (ctx, user.Id, email);
    }

    private static readonly byte[] AuthHash = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private static string AuthHashB64 => Convert.ToBase64String(AuthHash);

    private static string CurrentCode(CloudTestContext ctx, Guid userId)
    {
        var user = ctx.Db.Users.Single(u => u.Id == userId);
        byte[] secret = ctx.MfaProtector.Unprotect(user.MfaSecret!);
        return TotpService.ComputeCode(secret, TotpService.TimeStep(FixedNow));
    }

    // ── enroll ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enroll_GeneratesSecret_ButDoesNotActivate()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);

        var resp = await ctx.Mfa.EnrollAsync(userId, default);

        Assert.NotNull(resp);
        Assert.Equal(32, resp!.SecretBase32.Length);
        Assert.Contains("otpauth://totp/", resp.OtpauthUri);
        Assert.Contains(Uri.EscapeDataString($"RemoteOps:{email}"), resp.OtpauthUri);

        var user = ctx.Db.Users.Single(u => u.Id == userId);
        Assert.NotNull(user.MfaSecret);       // segredo gravado…
        Assert.False(user.MfaRequired);        // …mas 2FA ainda NÃO exigido
        Assert.Null(user.MfaEnrolledAt);
    }

    [Fact]
    public async Task Enroll_StoresSecretEncryptedAtRest()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, _) = await SeedE2eeUserAsync(ctx);

        var resp = await ctx.Mfa.EnrollAsync(userId, default);
        var user = ctx.Db.Users.Single(u => u.Id == userId);

        // O que está no banco é o blob cifrado (48B), não os 20B do segredo em claro.
        Assert.Equal(48, user.MfaSecret!.Length);
        byte[] plain = ctx.MfaProtector.Unprotect(user.MfaSecret);
        Assert.Equal(TotpService.ToBase32(plain), resp!.SecretBase32);
    }

    [Fact]
    public async Task Enroll_Rejected_WhenAlready2faActive()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, _) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        // Já ativo: re-enroll é recusado (senão trocaria o segredo ativo por um pendente e trancaria).
        var second = await ctx.Mfa.EnrollAsync(userId, default);
        Assert.Null(second);
    }

    // ── confirm ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_WithValidCode_ActivatesMfa()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, _) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);

        bool ok = await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        Assert.True(ok);
        var user = ctx.Db.Users.Single(u => u.Id == userId);
        Assert.True(user.MfaRequired);
        Assert.NotNull(user.MfaEnrolledAt);
    }

    [Fact]
    public async Task Confirm_WithWrongCode_DoesNotActivate()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, _) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);

        bool ok = await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest("000000"), default);

        Assert.False(ok);
        Assert.False(ctx.Db.Users.Single(u => u.Id == userId).MfaRequired);
    }

    [Fact]
    public async Task Confirm_Fails_WhenNotEnrolled()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, _) = await SeedE2eeUserAsync(ctx);

        bool ok = await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest("123456"), default);

        Assert.False(ok);
    }

    // ── login com 2FA ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithoutTotp_ReturnsMfaRequired_WhenActive()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        var result = await ctx.TokensAtFixedNow().LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D") { AuthHash = AuthHashB64 },
            "1.2.3.4", default);

        Assert.Equal(LoginOutcome.MfaRequired, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Login_WithValidTotp_Succeeds()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        var result = await ctx.TokensAtFixedNow().LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D")
            {
                AuthHash = AuthHashB64,
                TotpCode = CurrentCode(ctx, userId),
            },
            "1.2.3.4", default);

        Assert.Equal(LoginOutcome.Success, result.Outcome);
        Assert.NotNull(result.Response);
        Assert.False(string.IsNullOrEmpty(result.Response!.AccessToken));
    }

    [Fact]
    public async Task Login_WithWrongTotp_ReturnsMfaRequired()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        var result = await ctx.TokensAtFixedNow().LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D")
            {
                AuthHash = AuthHashB64,
                TotpCode = "000000",
            },
            "1.2.3.4", default);

        Assert.Equal(LoginOutcome.MfaRequired, result.Outcome);
    }

    [Fact]
    public async Task Login_WithWrongPassword_And2faActive_ReturnsInvalidCredentials_NotMfaRequired()
    {
        // 2FA não pode virar oráculo: senha errada devolve "credencial inválida", NUNCA "mfa_required"
        // (senão um atacante sem a senha descobriria que a conta tem 2FA).
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        var result = await ctx.TokensAtFixedNow().LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D")
            {
                AuthHash = Convert.ToBase64String(new byte[32]), // AuthHash errado
                TotpCode = CurrentCode(ctx, userId),
            },
            "1.2.3.4", default);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task Login_SameCodeTwice_IsAccepted_NoAntiReplay()
    {
        // Decisão documentada (TotpService): sem anti-replay — o mesmo código no mesmo passo passa 2x.
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        LoginRequest Req() => new(email, null, Guid.NewGuid().ToString(), "D")
        {
            AuthHash = AuthHashB64,
            TotpCode = CurrentCode(ctx, userId),
        };

        Assert.Equal(LoginOutcome.Success, (await ctx.TokensAtFixedNow().LoginAsync(Req(), "1.2.3.4", default)).Outcome);
        Assert.Equal(LoginOutcome.Success, (await ctx.TokensAtFixedNow().LoginAsync(Req(), "1.2.3.4", default)).Outcome);
    }

    // ── disable ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disable_WithValidCode_TurnsOffAndClearsSecret()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, email) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        bool ok = await ctx.Mfa.DisableAsync(userId, new MfaDisableRequest(CurrentCode(ctx, userId)), default);

        Assert.True(ok);
        var user = ctx.Db.Users.Single(u => u.Id == userId);
        Assert.False(user.MfaRequired);
        Assert.Null(user.MfaSecret);
        Assert.Null(user.MfaEnrolledAt);

        // E o login volta a passar só com a senha.
        var login = await ctx.TokensAtFixedNow().LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D") { AuthHash = AuthHashB64 },
            "1.2.3.4", default);
        Assert.Equal(LoginOutcome.Success, login.Outcome);
    }

    [Fact]
    public async Task Disable_WithWrongCode_KeepsMfaOn()
    {
        using var ctx = new CloudTestContext();
        var (_, userId, _) = await SeedE2eeUserAsync(ctx);
        await ctx.Mfa.EnrollAsync(userId, default);
        await ctx.Mfa.ConfirmAsync(userId, new MfaConfirmRequest(CurrentCode(ctx, userId)), default);

        bool ok = await ctx.Mfa.DisableAsync(userId, new MfaDisableRequest("000000"), default);

        Assert.False(ok);
        Assert.True(ctx.Db.Users.Single(u => u.Id == userId).MfaRequired);
    }
}
