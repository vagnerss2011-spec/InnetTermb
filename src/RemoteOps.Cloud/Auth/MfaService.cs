using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;

namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Ciclo de vida do 2FA/TOTP (spec Fase 3): enroll → confirm → disable. Todos os pontos de entrada
/// são AUTENTICADOS (o userId vem do JWT, não do corpo).
///
/// <para><b>Fronteira E2EE:</b> nada aqui toca o cofre. Ativar/desativar 2FA muda só a exigência de
/// prova de identidade no login; a chave do cofre continua sendo derivada da senha (ver TotpService).</para>
/// </summary>
public sealed class MfaService(
    AppDbContext db,
    MfaSecretProtector protector,
    ILogger<MfaService> logger)
{
    // Seam de teste: relógio para a validação do TOTP no confirm/disable.
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Gera um segredo TOTP e o guarda CIFRADO (pendente). NÃO ativa o 2FA — isso só acontece no
    /// <see cref="ConfirmAsync"/>. Null = não dá pra inscrever (conta ausente/inativa, ou 2FA já
    /// ativo — nesse caso o usuário tem que desativar antes, senão trocaríamos o segredo ativo por um
    /// não-confirmado e o trancaríamos fora).
    /// </summary>
    public async Task<MfaEnrollResponse?> EnrollAsync(Guid userId, CancellationToken ct)
    {
        var user = await FindActiveAsync(userId, ct);
        if (user is null)
        {
            return null;
        }

        if (user.MfaRequired)
        {
            logger.LogWarning("MFA enroll rejected: already active for user {UserId}", userId);
            return null;
        }

        byte[] secret = TotpService.GenerateSecret();
        try
        {
            // Sobrescreve um enroll pendente anterior (não-confirmado): é seguro porque MfaRequired
            // está false, então nenhum device depende do segredo antigo ainda.
            user.MfaSecret = protector.Protect(secret);
            user.MfaEnrolledAt = null;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("MFA enroll (pending) for user {UserId}", userId);
            return new MfaEnrollResponse(
                TotpService.ToBase32(secret),
                TotpService.BuildOtpauthUri(user.Email, secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Ativa o 2FA se o código bater com o segredo pendente. Idempotente por natureza.</summary>
    public async Task<bool> ConfirmAsync(Guid userId, MfaConfirmRequest req, CancellationToken ct)
    {
        var user = await FindActiveAsync(userId, ct);
        if (user?.MfaSecret is null)
        {
            logger.LogWarning("MFA confirm rejected: no pending secret for user {UserId}", userId);
            return false;
        }

        if (!VerifyAgainst(user.MfaSecret, req.Code, userId))
        {
            return false;
        }

        user.MfaRequired = true;
        user.MfaEnrolledAt = UtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("MFA confirmed (active) for user {UserId}", userId);
        return true;
    }

    /// <summary>Desliga o 2FA — exige um código TOTP válido (não basta estar logado). Limpa o segredo.</summary>
    public async Task<bool> DisableAsync(Guid userId, MfaDisableRequest req, CancellationToken ct)
    {
        var user = await FindActiveAsync(userId, ct);
        if (user?.MfaSecret is null)
        {
            logger.LogWarning("MFA disable rejected: not enrolled for user {UserId}", userId);
            return false;
        }

        if (!VerifyAgainst(user.MfaSecret, req.Code, userId))
        {
            return false;
        }

        user.MfaRequired = false;
        user.MfaSecret = null;
        user.MfaEnrolledAt = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("MFA disabled for user {UserId}", userId);
        return true;
    }

    private bool VerifyAgainst(byte[] encryptedSecret, string? code, Guid userId)
    {
        byte[] secret;
        try
        {
            secret = protector.Unprotect(encryptedSecret);
        }
        catch (CryptographicException)
        {
            logger.LogError("TOTP secret failed to unprotect for user {UserId}", userId);
            return false;
        }

        try
        {
            return TotpService.Verify(secret, code, UtcNow());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    private async Task<UserEntity?> FindActiveAsync(Guid userId, CancellationToken ct)
        => await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.Status == "active", ct);
}
