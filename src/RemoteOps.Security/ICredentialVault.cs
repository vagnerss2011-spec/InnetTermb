namespace RemoteOps.Security;

// Contrato fino cross-module do cofre. Implementado por
// RemoteOps.Security.Vault.CredentialVault (envelope encryption + DPAPI, ADR-003).
// Para a API rica do módulo (ReadOnlyMemory<char>, rotação, contexto de auditoria)
// use RemoteOps.Security.Vault.IVault. Nunca expõe segredo em logs.
public interface ICredentialVault
{
    Task<string?> RetrieveSecretAsync(string envelopeId, CancellationToken ct = default);

    Task<string> StoreSecretAsync(string secret, string workspaceId, CancellationToken ct = default);

    Task RevokeSecretAsync(string envelopeId, CancellationToken ct = default);
}
