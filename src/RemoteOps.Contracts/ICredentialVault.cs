using RemoteOps.Contracts.Models;

namespace RemoteOps.Contracts;

/// <summary>
/// Resolves a credential reference to an in-memory plaintext credential.
/// Implementations must source secrets from the encrypted vault only — never from disk plaintext.
/// </summary>
public interface ICredentialVault
{
    /// <summary>
    /// Returns a disposable credential. Caller must Dispose() as soon as the SSH handshake completes.
    /// Returns null when the ref is unknown or the vault is locked.
    /// </summary>
    Task<PlaintextCredential?> ResolveAsync(string credentialRefId, CancellationToken ct = default);
}
