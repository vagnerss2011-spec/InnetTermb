using System;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Security.Vault;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>Fake mínimo de IVault para testes de fiação (não faz criptografia real).</summary>
public sealed class FakeVault : IVault
{
    public Task<SecretEnvelope> StoreAsync(VaultStoreRequest r, ReadOnlyMemory<char> secret, CancellationToken ct = default)
        => Task.FromResult(Env("env-" + r.CredentialId, r.CredentialId, r.WorkspaceId));

    public Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default)
        => Task.FromResult(Env(envelopeId, "c", "ws-local"));

    public Task RevokeAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
        => Task.CompletedTask;

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
