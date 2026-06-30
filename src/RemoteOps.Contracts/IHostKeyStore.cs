using RemoteOps.Contracts.Models;

namespace RemoteOps.Contracts;

/// <summary>
/// Persists known SSH host keys (TOFU model).
/// Implementations write to an encrypted local store — never to plaintext.
/// </summary>
public interface IHostKeyStore
{
    /// <summary>Returns the stored fingerprint for this host, or null if unknown.</summary>
    Task<string?> GetFingerprintAsync(string host, CancellationToken ct = default);

    /// <summary>Stores or updates the fingerprint for a host after user acceptance.</summary>
    Task SaveFingerprintAsync(string host, string fingerprintSha256, string keyType, CancellationToken ct = default);

    /// <summary>Removes the stored key — used when the user permanently rejects a changed key.</summary>
    Task RemoveFingerprintAsync(string host, CancellationToken ct = default);
}
