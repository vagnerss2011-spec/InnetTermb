using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Email;
using static RemoteOps.Cloud.Auth.E2eeMaterialCodec;

namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Recuperação de senha por email (spec Fase 4).
///
/// A FRONTEIRA E2EE: o token do email prova só o controle do EMAIL → recupera ACESSO (autoriza
/// trocar a prova de senha sem o AuthHash antigo). Ele NÃO reabre o cofre: a AMK está selada sob a
/// KEK antiga, e o servidor nunca teve a AMK. Quem reabre o cofre é a CHAVE DE RECUPERAÇÃO, no
/// cliente, que desembrulha <c>wrapped_amk_rec</c> e re-embrulha a AMK sob a senha nova. O servidor
/// aqui só valida o token e grava o material novo que o cliente já computou. A AMK não muda.
/// </summary>
public sealed class PasswordResetService(
    AppDbContext db,
    IEmailSender email,
    ILogger<PasswordResetService> logger)
{
    private const int TokenByteLength = 32; // 256 bits — alta entropia, hash SHA-256 basta (sem PBKDF2).
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ResendCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Seam de teste do relógio (expiração/cooldown). Por padrão é o real.</summary>
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    // ── POST /auth/password/forgot ─────────────────────────────────────────────

    /// <summary>
    /// Se o email pertencer a uma conta E2EE ativa (com escrow de recuperação), gera um token de uso
    /// único e dispara o email. NÃO revela se a conta existe (o endpoint sempre devolve 202) e tem
    /// cooldown de reenvio por conta (anti email-bomb). Idempotente do ponto de vista do chamador.
    /// </summary>
    public async Task RequestAsync(string emailAddress, CancellationToken ct)
    {
        var normalized = EmailNormalizer.Normalize(emailAddress ?? string.Empty);

        // Só contas E2EE COM escrow de recuperação são recuperáveis: sem wrapped_amk_rec a chave de
        // recuperação não abriria a AMK, então mandar email seria inútil (e enganoso).
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == normalized
                 && u.Status == "active"
                 && u.AuthHashHash != null
                 && u.WrappedAmkRec != null,
            ct);

        if (user is null)
        {
            logger.LogInformation(
                "Password reset requested for unknown/non-recoverable email hash {EmailHash}",
                HashForLog(normalized));
            return;
        }

        var now = UtcNow();

        var activeTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct);

        // Cooldown: já mandamos um email há pouco? Não floodar a caixa da vítima.
        if (activeTokens.Any(t => now - t.CreatedAt < ResendCooldown))
        {
            logger.LogInformation("Password reset suppressed by cooldown for user {UserId}", user.Id);
            return;
        }

        // Só o token mais novo vale: supersede os anteriores (um token antigo vazado para de servir).
        db.PasswordResetTokens.RemoveRange(activeTokens);

        var rawToken = NewRawToken();
        db.PasswordResetTokens.Add(new PasswordResetTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            ExpiresAt = now.Add(TokenLifetime),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        await email.SendAsync(BuildRecoveryEmail(user.Email, rawToken), ct);
        logger.LogInformation("Password reset token issued for user {UserId}", user.Id);
    }

    // ── POST /auth/password/reset-context ──────────────────────────────────────

    /// <summary>
    /// Troca um token válido pelo escrow de recuperação (<c>wrapped_amk_rec</c>, base64) para o
    /// cliente abrir a AMK com a chave de recuperação. NÃO consome o token (o reset ainda vai usá-lo).
    /// Devolve <c>null</c> se o token for inválido/expirado/usado.
    /// </summary>
    public async Task<string?> GetResetContextAsync(string token, CancellationToken ct)
    {
        var entity = await FindActiveTokenAsync(token, ct);
        if (entity?.User is not { } user || user.Status != "active" || user.WrappedAmkRec is null)
            return null;

        return Convert.ToBase64String(user.WrappedAmkRec);
    }

    // ── POST /auth/password/reset ──────────────────────────────────────────────

    /// <summary>
    /// Conclui o reset: valida o token (uso único) e grava o material novo que o cliente já computou
    /// (mesma re-embrulhada do <c>password/change</c>, mas SEM o AuthHash antigo — o token do email é
    /// a autorização). A AMK e o escrow de recuperação ficam INTOCADOS. Marca o token usado, invalida
    /// os demais tokens de reset ativos e revoga TODOS os refresh tokens (reset desloga todo device).
    ///
    /// Devolve <c>false</c> só para token inválido/expirado/usado. Material novo inválido LANÇA
    /// <see cref="ArgumentException"/> (→ 400) para não deixar a conta meio-trocada e trancada.
    /// </summary>
    public async Task<bool> ResetAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        var token = await FindActiveTokenAsync(req.Token, ct);
        if (token?.User is not { } user || user.Status != "active" || user.AuthHashHash is null)
        {
            logger.LogWarning("Password reset rejected: token invalid/used/expired or user not E2EE");
            return false;
        }

        // Valida ANTES de mutar: material inválido não pode gravar salt/senha novos e deixar o escrow
        // velho — isso trancaria o cofre para sempre.
        var newSalt = DecodeExact(req.NewArgon2Salt, SaltLength, nameof(req.NewArgon2Salt));
        DecodeExact(req.NewAuthHash, AuthHashLength, nameof(req.NewAuthHash));
        var newWrapped = DecodeNonEmpty(req.NewWrappedAmkPwd, nameof(req.NewWrappedAmkPwd));
        ValidateParams(req.NewArgon2Params);

        var now = UtcNow();

        user.Argon2Salt = newSalt;
        user.Argon2MemoryKib = req.NewArgon2Params.MemoryKib;
        user.Argon2Iterations = req.NewArgon2Params.Iterations;
        user.Argon2Parallelism = req.NewArgon2Params.Parallelism;
        user.Argon2OutputBytes = req.NewArgon2Params.OutputBytes;
        user.AuthHashHash = PasswordHasher.Hash(req.NewAuthHash);
        user.WrappedAmkPwd = newWrapped;
        // WrappedAmkRec e AmkKeyVersion INTOCADOS: a AMK não muda; a chave de recuperação segue válida.

        token.UsedAt = now;

        var otherActiveTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.Id != token.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var t in otherActiveTokens) t.UsedAt = now;

        var sessions = await db.RefreshTokens
            .Where(r => r.UserId == user.Id && r.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var s in sessions) s.RevokedAt = now;

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Password reset completed (AMK rewrapped, {SessionCount} sessions revoked) for user {UserId}",
            sessions.Count, user.Id);
        return true;
    }

    // ── Interno ────────────────────────────────────────────────────────────────

    private async Task<PasswordResetTokenEntity?> FindActiveTokenAsync(string? token, CancellationToken ct)
    {
        var tokenHash = HashToken(token ?? string.Empty);
        var now = UtcNow();
        var entity = await db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

        if (entity is null || entity.UsedAt is not null || entity.ExpiresAt <= now)
            return null;
        return entity;
    }

    private static EmailMessage BuildRecoveryEmail(string toEmail, string rawToken) =>
        new(
            toEmail,
            "RemoteOps — recuperação de senha",
            "Você (ou alguém) pediu para recuperar a senha da sua conta RemoteOps.\n\n"
            + $"Código de recuperação:\n\n    {rawToken}\n\n"
            + "No RemoteOps: tela de login → \"Esqueci a senha\" → cole este código, informe a sua "
            + "CHAVE DE RECUPERAÇÃO e escolha uma nova senha. O código expira em 30 minutos e só pode "
            + "ser usado uma vez.\n\n"
            + "IMPORTANTE: só o código NÃO reabre as senhas dos seus equipamentos — você também "
            + "precisa da chave de recuperação (aquela que o RemoteOps mostrou uma única vez, ao criar "
            + "a conta). Sem ela, por segurança (E2EE), os dados cifrados não podem ser recuperados.\n\n"
            + "Se não foi você, ignore este email: nada muda até o código ser usado com a chave de "
            + "recuperação.");

    private static string NewRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        // Base64Url: cabe num email/URL sem escaping.
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HashForLog(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
