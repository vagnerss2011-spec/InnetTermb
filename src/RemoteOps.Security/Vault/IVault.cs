namespace RemoteOps.Security.Vault;

/// <summary>
/// Cofre de credenciais com envelope encryption. Interface rica do módulo
/// (usa <see cref="ReadOnlyMemory{Char}"/> para o segredo, evitando reter
/// <see cref="string"/> imutável na heap). O contrato cross-module fino é
/// <c>ICredentialVault</c>.
/// </summary>
public interface IVault
{
    /// <summary>Cifra e persiste um novo segredo. Retorna o envelope (sem plaintext).</summary>
    Task<SecretEnvelope> StoreAsync(VaultStoreRequest request, ReadOnlyMemory<char> secret, CancellationToken ct = default);

    /// <summary>Descriptografa um segredo. O chamador deve descartar o <see cref="VaultSecret"/>.</summary>
    Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext context, CancellationToken ct = default);

    /// <summary>
    /// Rotaciona o segredo: cria um novo envelope (versão incrementada) e revoga
    /// o anterior. Retorna o novo envelope; o chamador atualiza o CredentialRef.
    /// </summary>
    Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> newSecret, VaultAccessContext context, CancellationToken ct = default);

    /// <summary>Revoga imediatamente um envelope, tornando-o irrecuperável.</summary>
    Task RevokeAsync(string envelopeId, VaultAccessContext context, CancellationToken ct = default);
}
