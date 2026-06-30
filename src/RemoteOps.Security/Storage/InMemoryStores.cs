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

/// <summary>Store de WDKs protegidas em memória (testes e cenários voláteis).</summary>
public sealed class InMemoryWorkspaceKeyStore : IWorkspaceKeyStore
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

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
}
