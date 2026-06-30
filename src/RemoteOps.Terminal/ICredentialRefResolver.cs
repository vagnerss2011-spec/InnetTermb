using RemoteOps.Contracts.Assets;

namespace RemoteOps.Terminal;

/// <summary>
/// Resolve um CredentialRefId para os metadados da credencial (usuário, tipo, envelopeId).
/// O segredo real é obtido separadamente via IVault usando SecretEnvelopeId.
/// </summary>
public interface ICredentialRefResolver
{
    Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default);
}
