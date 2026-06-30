namespace RemoteOps.Sync.Remote;

/// <summary>
/// Persistência dos tokens de autenticação. Implementações nunca gravam o token em claro:
/// <see cref="VaultTokenStore"/> usa o vault (DPAPI/envelope, ADR-003).
/// </summary>
public interface ITokenStore
{
    Task<TokenSet?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(TokenSet tokens, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
