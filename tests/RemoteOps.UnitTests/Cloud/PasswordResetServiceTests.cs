using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Auth;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Testes da recuperação de senha por email (Fase 4). InMemory + serviços reais (padrão
/// CloudTestContext). O round-trip cripto real (chave de recuperação → nova senha → decifra segredo)
/// está em <see cref="PasswordResetCryptoRoundTripTests"/>.
///
/// LIMITAÇÃO: InMemory não é Postgres (sem índice único/concorrência reais) — ver §10 do spec.
/// </summary>
public sealed class PasswordResetServiceTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>Registra uma conta E2EE com blobs opacos aleatórios (ou um wrappedRec dado); devolve o email.</summary>
    private static async Task<string> SeedE2eeAccountAsync(CloudTestContext ctx, string email, byte[]? wrappedRec = null) =>
        (await ctx.Accounts.RegisterAsync(
            new RegisterRequest(
                Email: email,
                Argon2Salt: Convert.ToBase64String(Rand(16)),
                Argon2Params: new Argon2Params(65536, 3, 1, 32),
                AuthHash: Convert.ToBase64String(Rand(32)),
                WrappedAmkPwd: Convert.ToBase64String(Rand(60)),
                WrappedAmkRec: Convert.ToBase64String(wrappedRec ?? Rand(60)),
                AmkKeyVersion: 1,
                DeviceId: Guid.NewGuid().ToString(),
                DeviceName: "Device A",
                WorkspaceName: "WS"),
            "1.2.3.4", default)) is not null
            ? email
            : throw new InvalidOperationException("registro falhou no seed");

    /// <summary>Pesca o token Base64Url do corpo do email (linha indentada com o código).</summary>
    private static string ExtractToken(string body) =>
        body.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Length >= 40 && l.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    // ── RequestAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_IssuesToken_AndSendsEmail_ForE2eeAccount()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");

        await ctx.PasswordReset.RequestAsync(email, default);

        var token = Assert.Single(ctx.Db.PasswordResetTokens);
        Assert.Null(token.UsedAt);

        var msg = Assert.Single(ctx.Email.Sent);
        Assert.Equal(email, msg.ToEmail);

        // O corpo carrega o token cru; o banco guarda só o hash dele (nunca o cru).
        var raw = ExtractToken(msg.TextBody);
        Assert.Equal(Sha256Hex(raw), token.TokenHash);
        Assert.DoesNotContain(raw, token.TokenHash);
    }

    [Fact]
    public async Task Request_IsSilent_ForUnknownEmail()
    {
        using var ctx = new CloudTestContext();

        await ctx.PasswordReset.RequestAsync("naoexiste@test.local", default);

        Assert.Empty(ctx.Db.PasswordResetTokens);
        Assert.Empty(ctx.Email.Sent);
    }

    [Fact]
    public async Task Request_IssuesNothing_ForLegacyAccount()
    {
        using var ctx = new CloudTestContext();
        var (_, _, user, _) = await ctx.SeedActiveUserAsync();
        user.PasswordHash = PasswordHasher.Hash("legado"); // pragma: allowlist secret
        await ctx.Db.SaveChangesAsync();

        // Conta legada não tem escrow de recuperação: mandar email de reset seria inútil e enganoso.
        await ctx.PasswordReset.RequestAsync(user.Email, default);

        Assert.Empty(ctx.Db.PasswordResetTokens);
        Assert.Empty(ctx.Email.Sent);
    }

    [Fact]
    public async Task Request_Cooldown_SuppressesSecondEmail_ThenSupersedesAfterCooldown()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");

        var now = CloudTestContext.FixedNow;
        var svc = new PasswordResetService(ctx.Db, ctx.Email, NullLogger<PasswordResetService>.Instance)
        {
            UtcNow = () => now,
        };

        await svc.RequestAsync(email, default);
        now += TimeSpan.FromSeconds(30); // dentro do cooldown (1 min)
        await svc.RequestAsync(email, default);

        // Segundo pedido suprimido: só 1 email, só 1 token.
        Assert.Single(ctx.Email.Sent);
        Assert.Single(ctx.Db.PasswordResetTokens);

        now += TimeSpan.FromMinutes(2); // passou o cooldown
        await svc.RequestAsync(email, default);

        // Novo email; o token antigo foi superseçado (removido) → segue com 1 token ativo.
        Assert.Equal(2, ctx.Email.Sent.Count);
        Assert.Single(ctx.Db.PasswordResetTokens);
        Assert.Equal(Sha256Hex(ExtractToken(ctx.Email.Last!.TextBody)), ctx.Db.PasswordResetTokens.Single().TokenHash);
    }

    // ── GetResetContextAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task Context_ReturnsWrappedAmkRec_ForValidToken()
    {
        using var ctx = new CloudTestContext();
        var wrappedRec = Rand(60);
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local", wrappedRec);

        await ctx.PasswordReset.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        var context = await ctx.PasswordReset.GetResetContextAsync(raw, default);

        // O cliente precisa exatamente do wrapped_amk_rec para abrir a AMK com a chave de recuperação.
        Assert.Equal(Convert.ToBase64String(wrappedRec), context);
    }

    [Fact]
    public async Task Context_DoesNotConsumeToken()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");
        await ctx.PasswordReset.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        Assert.NotNull(await ctx.PasswordReset.GetResetContextAsync(raw, default));
        Assert.NotNull(await ctx.PasswordReset.GetResetContextAsync(raw, default));
        Assert.Null(ctx.Db.PasswordResetTokens.Single().UsedAt);
    }

    [Fact]
    public async Task Context_ReturnsNull_ForUnknownToken()
    {
        using var ctx = new CloudTestContext();
        await SeedE2eeAccountAsync(ctx, "operador@test.local");

        Assert.Null(await ctx.PasswordReset.GetResetContextAsync("token-que-nao-existe", default));
    }

    [Fact]
    public async Task Context_ReturnsNull_ForExpiredToken()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");

        var now = CloudTestContext.FixedNow;
        var svc = new PasswordResetService(ctx.Db, ctx.Email, NullLogger<PasswordResetService>.Instance)
        {
            UtcNow = () => now,
        };
        await svc.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        now += TimeSpan.FromMinutes(31); // TTL do token = 30 min
        Assert.Null(await svc.GetResetContextAsync(raw, default));
    }

    [Fact]
    public async Task Context_ReturnsNull_ForUsedToken()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");
        await ctx.PasswordReset.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        // Marca como consumido (simula um reset concluído).
        ctx.Db.PasswordResetTokens.Single().UsedAt = CloudTestContext.FixedNow;
        await ctx.Db.SaveChangesAsync();

        Assert.Null(await ctx.PasswordReset.GetResetContextAsync(raw, default));
    }

    // ── ResetAsync (material opaco; o round-trip cripto real está na classe abaixo) ──

    private static ResetPasswordRequest ValidReset(string token, byte[]? newAuthHash = null, byte[]? newWrapped = null) =>
        new(
            Token: token,
            NewAuthHash: Convert.ToBase64String(newAuthHash ?? Rand(32)),
            NewArgon2Salt: Convert.ToBase64String(Rand(16)),
            NewArgon2Params: new Argon2Params(65536, 3, 1, 32),
            NewWrappedAmkPwd: Convert.ToBase64String(newWrapped ?? Rand(60)));

    [Fact]
    public async Task Reset_Rewraps_KeepsRecoveryEscrow_AndIsSingleUse()
    {
        using var ctx = new CloudTestContext();
        var wrappedRec = Rand(60);
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local", wrappedRec);
        await ctx.PasswordReset.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        var newAuthHash = Rand(32);
        var newWrapped = Rand(60);
        var ok = await ctx.PasswordReset.ResetAsync(ValidReset(raw, newAuthHash, newWrapped), default);
        Assert.True(ok);

        var user = ctx.Db.Users.Single();
        Assert.Equal(newWrapped, user.WrappedAmkPwd);
        Assert.True(PasswordHasher.Verify(Convert.ToBase64String(newAuthHash), user.AuthHashHash));

        // A AMK não muda: escrow de recuperação e versão intocados.
        Assert.Equal(wrappedRec, user.WrappedAmkRec);
        Assert.Equal(1, user.AmkKeyVersion);

        // Token consumido → uso único: um segundo reset com o mesmo token falha.
        Assert.NotNull(ctx.Db.PasswordResetTokens.Single().UsedAt);
        Assert.False(await ctx.PasswordReset.ResetAsync(ValidReset(raw), default));
    }

    [Fact]
    public async Task Reset_Fails_ForInvalidToken_WithoutMutating()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");
        var before = ctx.Db.Users.Single().WrappedAmkPwd;

        Assert.False(await ctx.PasswordReset.ResetAsync(ValidReset("nao-existe"), default));
        Assert.Equal(before, ctx.Db.Users.Single().WrappedAmkPwd);
    }

    [Fact]
    public async Task Reset_Fails_ForExpiredToken()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");

        var now = CloudTestContext.FixedNow;
        var svc = new PasswordResetService(ctx.Db, ctx.Email, NullLogger<PasswordResetService>.Instance)
        {
            UtcNow = () => now,
        };
        await svc.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        now += TimeSpan.FromMinutes(31);
        Assert.False(await svc.ResetAsync(ValidReset(raw), default));
    }

    [Fact]
    public async Task Reset_RevokesAllRefreshTokens()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");
        await ctx.PasswordReset.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);

        // O registro emitiu uma sessão → há refresh token(s) ativos.
        Assert.NotEmpty(ctx.Db.RefreshTokens);
        Assert.All(ctx.Db.RefreshTokens, r => Assert.Null(r.RevokedAt));

        Assert.True(await ctx.PasswordReset.ResetAsync(ValidReset(raw), default));

        // Reset desloga todo device: nenhum refresh token da conta segue válido.
        Assert.All(ctx.Db.RefreshTokens, r => Assert.NotNull(r.RevokedAt));
    }

    [Fact]
    public async Task Reset_InvalidMaterial_Throws_AndDoesNotConsumeTokenNorMutate()
    {
        using var ctx = new CloudTestContext();
        var email = await SeedE2eeAccountAsync(ctx, "operador@test.local");
        await ctx.PasswordReset.RequestAsync(email, default);
        var raw = ExtractToken(ctx.Email.Last!.TextBody);
        var before = ctx.Db.Users.Single().WrappedAmkPwd;

        // AuthHash com tamanho errado (16B em vez de 32B): validação ANTES de mutar.
        var bad = new ResetPasswordRequest(
            Token: raw,
            NewAuthHash: Convert.ToBase64String(Rand(16)),
            NewArgon2Salt: Convert.ToBase64String(Rand(16)),
            NewArgon2Params: new Argon2Params(65536, 3, 1, 32),
            NewWrappedAmkPwd: Convert.ToBase64String(Rand(60)));

        await Assert.ThrowsAsync<ArgumentException>(() => ctx.PasswordReset.ResetAsync(bad, default));

        // Nada mutado; token NÃO consumido (o operador pode tentar de novo com material válido).
        Assert.Equal(before, ctx.Db.Users.Single().WrappedAmkPwd);
        Assert.Null(ctx.Db.PasswordResetTokens.Single().UsedAt);
    }
}
