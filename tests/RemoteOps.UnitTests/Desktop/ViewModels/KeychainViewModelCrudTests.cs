using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Vault;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class KeychainViewModelCrudTests
{
    private sealed class FakeVault : IVault
    {
        public string? LastStoredSecret;
        public string? LastRotatedId;
        public string? LastRevokedId;

        public Task<SecretEnvelope> StoreAsync(VaultStoreRequest r, ReadOnlyMemory<char> secret, CancellationToken ct = default)
        {
            LastStoredSecret = new string(secret.Span);
            return Task.FromResult(Env("env-" + r.CredentialId, r.CredentialId, r.WorkspaceId));
        }

        public Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default)
        {
            LastRotatedId = envelopeId;
            return Task.FromResult(Env(envelopeId, "c", "ws-local"));
        }

        public Task RevokeAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
        {
            LastRevokedId = envelopeId;
            return Task.CompletedTask;
        }

        private static SecretEnvelope Env(string id, string cid, string ws) => new()
        {
            EnvelopeId = id, WorkspaceId = ws, CredentialId = cid, Type = "password", Version = 1, Algorithm = "test",
            WrappedCek = [], CekNonce = [], CekTag = [], Ciphertext = [], Nonce = [], Tag = [], CreatedAt = default,
        };
    }

    [Fact]
    public async Task Create_StoresSecretInVault_AndAddsRef()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var vm = new KeychainViewModel(store, vault, "ws-local");
        await vm.CreateAsync("root@r1", "root", "s3cr3t".ToCharArray());
        var refs = await store.GetCredentialRefsAsync("ws-local");
        var cred = refs.Single();
        Assert.Equal("root@r1", cred.Name);
        Assert.Equal("root", cred.Metadata!.Username);
        Assert.StartsWith("env-", cred.SecretEnvelopeId);
        Assert.Equal("s3cr3t", vault.LastStoredSecret);
    }

    [Fact]
    public async Task Delete_RevokesEnvelope_AndRemovesRef()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var vm = new KeychainViewModel(store, vault, "ws-local");
        await vm.CreateAsync("c", "u", "p".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.DeleteAsync(cred);
        Assert.Empty(await store.GetCredentialRefsAsync("ws-local"));
        Assert.Equal(cred.SecretEnvelopeId, vault.LastRevokedId);
    }

    [Fact]
    public async Task Update_ChangesNameAndUsername_KeepsIdAndEnvelope()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var vm = new KeychainViewModel(store, vault, "ws-local");
        await vm.CreateAsync("old", "olduser", "p".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.UpdateAsync(cred, "new", "newuser");
        var updated = (await store.GetCredentialRefsAsync("ws-local")).Single();
        Assert.Equal(cred.Id, updated.Id);
        Assert.Equal("new", updated.Name);
        Assert.Equal("newuser", updated.Metadata!.Username);
        Assert.Equal(cred.SecretEnvelopeId, updated.SecretEnvelopeId);
    }
}
