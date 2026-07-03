using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Security.Vault;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>Fake mínimo de IVault para testes de fiação (não faz criptografia real).</summary>
public sealed class FakeVault : IVault
{
    public List<string> StoredCredentialIds { get; } = [];
    public List<string> RotatedEnvelopeIds { get; } = [];
    public List<string?> RevokedEnvelopeIds { get; } = [];

    public Task<SecretEnvelope> StoreAsync(VaultStoreRequest r, ReadOnlyMemory<char> secret, CancellationToken ct = default)
    {
        StoredCredentialIds.Add(r.CredentialId);
        return Task.FromResult(Env("env-" + r.CredentialId, r.CredentialId, r.WorkspaceId));
    }

    public Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default)
    {
        RotatedEnvelopeIds.Add(envelopeId);
        return Task.FromResult(Env(envelopeId, "c", "ws-local"));
    }

    public Task RevokeAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
    {
        RevokedEnvelopeIds.Add(envelopeId);
        return Task.CompletedTask;
    }

    private static SecretEnvelope Env(string id, string cid, string ws) => new()
    {
        EnvelopeId = id,
        WorkspaceId = ws,
        CredentialId = cid,
        Type = "password",
        Version = 1,
        Algorithm = "test",
        WrappedCek = [],
        CekNonce = [],
        CekTag = [],
        Ciphertext = [],
        Nonce = [],
        Tag = [],
        CreatedAt = default,
    };
}
