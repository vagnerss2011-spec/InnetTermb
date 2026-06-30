namespace RemoteOps.Sync.Storage;

/// <summary>
/// Abstração sobre a obtenção/criação da chave de criptografia do banco local.
/// A implementação de produção delega ao vault (DPAPI/envelope, ADR-003).
/// Implementações de teste podem retornar uma chave determinística.
/// </summary>
internal interface IDbKeyProvider
{
    /// <summary>
    /// Retorna a chave como string hex de 64 chars (32 bytes / AES-256).
    /// Na primeira chamada para <paramref name="workspaceId"/>, gera e persiste uma nova chave.
    /// Nunca loga o material de chave.
    /// </summary>
    Task<string> GetOrCreateKeyAsync(string workspaceId, CancellationToken ct = default);
}
