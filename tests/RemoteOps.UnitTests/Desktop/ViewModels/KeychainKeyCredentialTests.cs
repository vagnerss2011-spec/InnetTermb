using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class KeychainKeyCredentialTests
{
    private static (KeychainViewModel vm, InMemoryLocalStore store, FakeVault vault) Build()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        return (new KeychainViewModel(store, vault, "ws-local", "ws-local"), store, vault);
    }

    [Fact]
    public async Task CreateKey_WithPassphrase_StoresTwoEnvelopes_AndMetadata()
    {
        var (vm, store, _) = Build();
        await vm.CreateKeyAsync("router-key", "root", "PRIVATEKEY".ToCharArray(), "pass".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        Assert.Equal(CredentialTypes.PrivateKey, cred.Type);
        Assert.True(cred.Metadata!.HasPrivateKey);
        Assert.Equal("root", cred.Metadata.Username);
        Assert.NotNull(cred.SecretEnvelopeId);
        Assert.NotNull(cred.Metadata.PassphraseEnvelopeId);
    }

    [Fact]
    public async Task CreateKey_NoPassphrase_LeavesPassphraseEnvelopeNull()
    {
        var (vm, store, _) = Build();
        await vm.CreateKeyAsync("k", "root", "PRIVATEKEY".ToCharArray(), passphrase: null);
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        Assert.Null(cred.Metadata!.PassphraseEnvelopeId);
    }

    [Fact]
    public async Task ChangePassword_OnKeyCredential_DoesNothing()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("k", "root", "PK".ToCharArray(), null);
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        vault.RotatedEnvelopeIds.Clear();
        await vm.ChangePasswordAsync(cred, "x".ToCharArray());
        Assert.Empty(vault.RotatedEnvelopeIds);
    }

    [Fact]
    public async Task ReplaceKey_RotatesKeyEnvelope()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("k", "root", "PK".ToCharArray(), null);
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.ReplaceKeyAsync(cred, "NEWKEY".ToCharArray());
        Assert.Contains(cred.SecretEnvelopeId, vault.RotatedEnvelopeIds);
    }

    [Fact]
    public async Task ChangePassphrase_WhenExists_Rotates()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("k", "root", "PK".ToCharArray(), "pp".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.ChangePassphraseAsync(cred, "np".ToCharArray());
        Assert.Contains(cred.Metadata!.PassphraseEnvelopeId, vault.RotatedEnvelopeIds);
    }

    [Fact]
    public async Task Delete_KeyWithPassphrase_RevokesBothEnvelopes()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("k", "root", "PK".ToCharArray(), "pp".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.DeleteAsync(cred);
        Assert.Contains(cred.SecretEnvelopeId, vault.RevokedEnvelopeIds);
        Assert.Contains(cred.Metadata!.PassphraseEnvelopeId, vault.RevokedEnvelopeIds);
    }
}
