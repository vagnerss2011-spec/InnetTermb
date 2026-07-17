namespace RemoteOps.Sync.Remote;

/// <summary>
/// Gestão do 2FA/TOTP da conta (spec Fase 3) — endpoints AUTENTICADOS (rodam com a sessão já ativa,
/// via Bearer). Separado do <see cref="IAccountApi"/> (anônimo, produz a sessão) e do
/// <see cref="ICloudSyncApi"/> (o sync contínuo): ativar/desativar 2FA é uma ação pontual do operador.
/// </summary>
public interface IMfaApi
{
    /// <summary>
    /// Gera um segredo TOTP e devolve o Base32 + otpauth URI pra o operador escanear/digitar. NÃO
    /// ativa o 2FA — só o <see cref="ConfirmAsync"/> ativa.
    /// </summary>
    Task<MfaEnrollResponse> EnrollAsync(CancellationToken ct = default);

    /// <summary>Ativa o 2FA confirmando que o app do operador gera os códigos certos.</summary>
    Task ConfirmAsync(MfaConfirmRequest request, CancellationToken ct = default);

    /// <summary>Desliga o 2FA — exige um código TOTP válido (não basta estar logado).</summary>
    Task DisableAsync(MfaDisableRequest request, CancellationToken ct = default);
}
