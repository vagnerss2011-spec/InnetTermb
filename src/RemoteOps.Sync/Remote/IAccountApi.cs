namespace RemoteOps.Sync.Remote;

/// <summary>
/// Endpoints de CONTA (registro/login E2EE) — separados de <see cref="ICloudSyncApi"/> de propósito:
/// são anônimos (não têm Bearer pra mandar; são justamente o que PRODUZ o token) e têm ciclo de vida
/// próprio — rodam uma vez no login e nunca mais, enquanto o sync roda o tempo todo. Implementações
/// nunca recebem a senha do operador: só authHash e blobs opacos (spec §4.2).
/// </summary>
public interface IAccountApi
{
    /// <summary>Cria conta + primeiro workspace a partir do material de enrollment (spec §6).</summary>
    Task<RegisterAccountResponse> RegisterAsync(RegisterAccountRequest request, CancellationToken ct = default);

    /// <summary>Salt/params do Argon2 da conta — pré-login, pra o device derivar a MasterKey.</summary>
    Task<KdfResponse> GetKdfAsync(string email, CancellationToken ct = default);

    /// <summary>Autentica pelo authHash e devolve tokens + o escrow da AMK por senha.</summary>
    Task<E2eeLoginResponse> LoginAsync(E2eeLoginRequest request, CancellationToken ct = default);
}
