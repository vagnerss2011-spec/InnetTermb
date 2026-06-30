using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>
/// Persistência dos envelopes cifrados. Armazena apenas ciphertext e metadados —
/// nunca plaintext. A implementação durável (SQLCipher) pertence à frente
/// feature/sync-local; aqui ficam as abstrações e implementações de referência.
/// </summary>
public interface ICredentialStore
{
    Task SaveAsync(SecretEnvelope envelope, CancellationToken ct = default);

    Task<SecretEnvelope?> GetAsync(string envelopeId, CancellationToken ct = default);

    Task DeleteAsync(string envelopeId, CancellationToken ct = default);
}
