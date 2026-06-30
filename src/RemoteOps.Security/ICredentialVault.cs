namespace RemoteOps.Security;

// TODO: Implementar na frente feature/security-vault (ver ADR-003).
// Responsável por envelope encryption + DPAPI. Nunca expõe segredo em logs.
public interface ICredentialVault
{
    Task<string?> RetrieveSecretAsync(string envelopeId, CancellationToken ct = default);

    Task<string> StoreSecretAsync(string secret, string workspaceId, CancellationToken ct = default);

    Task RevokeSecretAsync(string envelopeId, CancellationToken ct = default);
}
