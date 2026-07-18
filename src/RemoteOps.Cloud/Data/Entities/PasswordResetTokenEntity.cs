namespace RemoteOps.Cloud.Data.Entities;

/// <summary>
/// Token de recuperação de senha por email (Fase 4). Espelha <see cref="RefreshTokenEntity"/>:
/// guarda só o HASH SHA-256 do token cru (o token viaja no email do dono e nunca toca o disco).
///
/// Uso ÚNICO (<see cref="UsedAt"/>) + TTL curto (<see cref="ExpiresAt"/>). Prova só o controle do
/// EMAIL (recuperação de ACESSO); o cofre continua trancado até a chave de recuperação abrir a AMK.
/// </summary>
public sealed class PasswordResetTokenEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Hash SHA-256 (hex) do token; nunca o valor em claro.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Quando foi consumido no reset. Nulo = ainda válido. Uso único.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public UserEntity User { get; set; } = null!;
}
