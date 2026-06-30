using System.Collections.Concurrent;

using RemoteOps.Security;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Dublê de <see cref="ICredentialVault"/> para os testes de sync.
/// Armazena segredos em memória (sem criptografia), permitindo isolar
/// o comportamento do LocalSyncClient sem depender do DPAPI.
/// Análogo a FakeKeyProtector nos testes de Security.
/// </summary>
internal sealed class FakeCredentialVault : ICredentialVault
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public Task<string> StoreSecretAsync(string secret, string workspaceId, CancellationToken ct = default)
    {
        string envelopeId = Guid.NewGuid().ToString("n");
        _store[envelopeId] = secret;
        return Task.FromResult(envelopeId);
    }

    public Task<string?> RetrieveSecretAsync(string envelopeId, CancellationToken ct = default)
    {
        _store.TryGetValue(envelopeId, out string? secret);
        return Task.FromResult(secret);
    }

    public Task RevokeSecretAsync(string envelopeId, CancellationToken ct = default)
    {
        _store.TryRemove(envelopeId, out _);
        return Task.CompletedTask;
    }
}
