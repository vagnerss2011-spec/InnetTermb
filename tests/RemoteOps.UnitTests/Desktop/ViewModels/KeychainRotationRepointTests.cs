using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// Rotacionar um segredo cria um envelope com ID NOVO e tombstoneia o antigo
/// (<c>CredentialVault.RotateAsync</c>). Se o <see cref="CredentialRef"/> não for repontado para o
/// envelope novo, ele fica apontando pro tombstone e o estrago é duplo: conectar falha NESTE PC
/// ("Envelope revogado") e, como nenhum metadado muda, nada entra no outbox — a troca de senha
/// nunca chega ao outro device. Estes testes prendem o repontamento.
/// </summary>
public sealed class KeychainRotationRepointTests
{
    private static (KeychainViewModel Vm, InMemoryLocalStore Store, FakeVault Vault) Build()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        return (new KeychainViewModel(store, vault, "ws-local"), store, vault);
    }

    private static async Task<CredentialRef> SavedAsync(InMemoryLocalStore store)
        => (await store.GetCredentialRefsAsync("ws-local")).Single();

    [Fact]
    public async Task ChangePassword_Repoints_CredentialRef_To_The_New_Envelope()
    {
        var (vm, store, vault) = Build();
        await vm.CreateAsync("root@r1", "root", "velha".ToCharArray());
        CredentialRef cred = await SavedAsync(store);
        string oldEnvelopeId = cred.SecretEnvelopeId!;

        await vm.ChangePasswordAsync(cred, "nova".ToCharArray());

        Assert.Equal(oldEnvelopeId, Assert.Single(vault.RotatedEnvelopeIds));
        CredentialRef saved = await SavedAsync(store);
        Assert.Equal(Assert.Single(vault.RotatedIntoEnvelopeIds), saved.SecretEnvelopeId);
        Assert.NotEqual(oldEnvelopeId, saved.SecretEnvelopeId);
    }

    [Fact]
    public async Task ChangePassword_Repointing_Preserves_The_Other_Metadata()
    {
        // O repontamento monta um CredentialRef novo à mão (a classe não é record): se algum campo
        // ficar de fora, a "correção" apaga nome/usuário do operador.
        var (vm, store, _) = Build();
        await vm.CreateAsync("root@r1", "root", "velha".ToCharArray());
        CredentialRef cred = await SavedAsync(store);

        await vm.ChangePasswordAsync(cred, "nova".ToCharArray());

        CredentialRef saved = await SavedAsync(store);
        Assert.Equal(cred.Id, saved.Id);
        Assert.Equal("root@r1", saved.Name);
        Assert.Equal(CredentialTypes.Password, saved.Type);
        Assert.Equal("root", saved.Metadata!.Username);
    }

    [Fact]
    public async Task ReplaceKey_Repoints_CredentialRef_To_The_New_Key_Envelope()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("router-key", "root", "PRIVATEKEY".ToCharArray(), "pp".ToCharArray());
        CredentialRef cred = await SavedAsync(store);
        string oldKeyEnvelopeId = cred.SecretEnvelopeId!;
        string passphraseEnvelopeId = cred.Metadata!.PassphraseEnvelopeId!;

        await vm.ReplaceKeyAsync(cred, "NOVACHAVE".ToCharArray());

        CredentialRef saved = await SavedAsync(store);
        Assert.Equal(Assert.Single(vault.RotatedIntoEnvelopeIds), saved.SecretEnvelopeId);
        Assert.NotEqual(oldKeyEnvelopeId, saved.SecretEnvelopeId);
        // Trocar a chave não pode arrastar a passphrase junto: ela tem envelope próprio.
        Assert.Equal(passphraseEnvelopeId, saved.Metadata!.PassphraseEnvelopeId);
        Assert.True(saved.Metadata.HasPrivateKey);
        Assert.Equal("root", saved.Metadata.Username);
    }

    [Fact]
    public async Task ChangePassphrase_WhenEnvelopeExists_Repoints_PassphraseEnvelopeId()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("router-key", "root", "PRIVATEKEY".ToCharArray(), "pp".ToCharArray());
        CredentialRef cred = await SavedAsync(store);
        string keyEnvelopeId = cred.SecretEnvelopeId!;
        string oldPassphraseEnvelopeId = cred.Metadata!.PassphraseEnvelopeId!;

        await vm.ChangePassphraseAsync(cred, "novapp".ToCharArray());

        Assert.Equal(oldPassphraseEnvelopeId, Assert.Single(vault.RotatedEnvelopeIds));
        CredentialRef saved = await SavedAsync(store);
        Assert.Equal(Assert.Single(vault.RotatedIntoEnvelopeIds), saved.Metadata!.PassphraseEnvelopeId);
        Assert.NotEqual(oldPassphraseEnvelopeId, saved.Metadata.PassphraseEnvelopeId);
        // A chave em si não rotacionou — só a passphrase.
        Assert.Equal(keyEnvelopeId, saved.SecretEnvelopeId);
    }

    [Fact]
    public async Task ChangePassword_Twice_Rotates_The_Live_Envelope_Not_The_Tombstone()
    {
        // A lista da tela é a origem do CredentialRef que o usuário edita. Se ela continuar com o
        // envelope morto depois da primeira troca, a segunda troca na mesma sessão bate no
        // tombstone e explode — repontar o banco sem recarregar a lista não resolve.
        var (vm, store, vault) = Build();
        await vm.CreateAsync("root@r1", "root", "velha".ToCharArray());
        await vm.ChangePasswordAsync(vm.Credentials.Single(), "nova".ToCharArray());

        await vm.ChangePasswordAsync(vm.Credentials.Single(), "novissima".ToCharArray());

        Assert.Equal(2, vault.RotatedEnvelopeIds.Count);
        Assert.Equal(vault.RotatedIntoEnvelopeIds[0], vault.RotatedEnvelopeIds[1]);
        CredentialRef saved = await SavedAsync(store);
        Assert.Equal(vault.RotatedIntoEnvelopeIds[1], saved.SecretEnvelopeId);
    }
}
