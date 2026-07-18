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

    /// <summary>Registra uma conta E2EE com blobs opacos aleatórios; devolve o email usado.</summary>
    private static async Task<string> SeedE2eeAccountAsync(CloudTestContext ctx, string email) =>
        (await ctx.Accounts.RegisterAsync(
            new RegisterRequest(
                Email: email,
                Argon2Salt: Convert.ToBase64String(Rand(16)),
                Argon2Params: new Argon2Params(65536, 3, 1, 32),
                AuthHash: Convert.ToBase64String(Rand(32)),
                WrappedAmkPwd: Convert.ToBase64String(Rand(60)),
                WrappedAmkRec: Convert.ToBase64String(Rand(60)),
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
}
