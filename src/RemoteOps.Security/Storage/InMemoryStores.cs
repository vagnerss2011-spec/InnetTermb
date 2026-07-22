using System.Collections.Concurrent;

using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>Store de envelopes em memória (testes e cenários voláteis).</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, SecretEnvelope> _envelopes = new(StringComparer.Ordinal);

    public Task SaveAsync(SecretEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _envelopes[envelope.EnvelopeId] = envelope;
        return Task.CompletedTask;
    }

    public Task<SecretEnvelope?> GetAsync(string envelopeId, CancellationToken ct = default)
    {
        _envelopes.TryGetValue(envelopeId, out SecretEnvelope? envelope);
        return Task.FromResult(envelope);
    }

    public Task DeleteAsync(string envelopeId, CancellationToken ct = default)
    {
        _envelopes.TryRemove(envelopeId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Store de WDKs protegidas em memória (testes e cenários voláteis).
///
/// <para>Atende também o <see cref="IVaultRootingStore"/> — as duas portas no MESMO objeto, como o
/// <see cref="FileVaultStore"/> faz em produção. É de propósito: chave e marcador de raiz precisam
/// aterrissar no mesmo lugar, e um duplo em memória que os separasse permitiria exercitar uma
/// montagem que o app não tem.</para>
/// </summary>
public sealed class InMemoryWorkspaceKeyStore : IWorkspaceKeyStore, IVaultRootingStore
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VaultKeyRooting> _rooting = new(StringComparer.Ordinal);

    public Task<byte[]?> LoadAsync(string workspaceId, CancellationToken ct = default)
    {
        _keys.TryGetValue(workspaceId, out byte[]? blob);
        return Task.FromResult(blob);
    }

    public Task SaveAsync(string workspaceId, byte[] protectedKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        _keys[workspaceId] = protectedKey;
        return Task.CompletedTask;
    }

    public Task<VaultKeyRooting?> LoadKeyRootingAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return Task.FromResult(
            _rooting.TryGetValue(workspaceId, out VaultKeyRooting rooting) ? rooting : (VaultKeyRooting?)null);
    }

    public Task SaveKeyRootingAsync(string workspaceId, VaultKeyRooting rooting, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _rooting[workspaceId] = rooting;
        return Task.CompletedTask;
    }
}
