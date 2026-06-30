using RemoteOps.Contracts.Assets;

namespace RemoteOps.Rdp;

/// <summary>
/// Resolve metadados da credencial (usuário) — NUNCA toca o vault. O segredo é
/// obtido separadamente via <see cref="IRdpCredentialResolver"/> apenas no momento
/// de conectar (lifetime mínimo).
/// </summary>
public interface IRdpCredentialRefResolver
{
    Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default);
}
