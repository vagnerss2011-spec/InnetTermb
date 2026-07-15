using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Credentials;

/// <summary>
/// Credencial "inline" (senha só deste device): cifrada no cofre, presa ao endpoint e escondida
/// do Keychain. Cobre o serviço e o fluxo do editor de host (materialização no Salvar + cascata).
/// </summary>
public sealed class InlineCredentialTests
{
    [Fact]
    public async Task Create_HiddenFromWorkspaceList_ButResolvableById()
    {
        var store = new InMemoryLocalStore();
        var svc = new InlineCredentialService(store, new FakeVault());

        string credId = await svc.CreateForEndpointAsync("ws-local", "ep-1", "admin", "p".ToCharArray());

        // Resolvível por id (é assim que o provider SSH/Telnet a busca para conectar)...
        Assert.NotNull(await store.GetCredentialRefAsync(credId));
        // ...mas NÃO aparece na lista do workspace (Keychain e dropdown do editor).
        var list = await store.GetCredentialRefsAsync("ws-local");
        Assert.DoesNotContain(list, c => c.Id == credId);
    }

    [Fact]
    public async Task Create_StoresSecretInVault_WithEndpointScope()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var svc = new InlineCredentialService(store, vault);

        string credId = await svc.CreateForEndpointAsync("ws-local", "ep-42", "root", "x".ToCharArray());

        Assert.Contains(credId, vault.StoredCredentialIds); // segredo foi pro cofre
        var cred = await store.GetCredentialRefAsync(credId);
        Assert.Equal("root", cred!.Metadata?.Username);
        Assert.Equal("endpoint:ep-42", cred.Scope);
        Assert.Equal(CredentialTypes.Password, cred.Type);
    }

    [Fact]
    public async Task Create_ZeroesPasswordBuffer()
    {
        var store = new InMemoryLocalStore();
        var svc = new InlineCredentialService(store, new FakeVault());

        char[] pw = "secret".ToCharArray();
        await svc.CreateForEndpointAsync("ws-local", "ep-1", "u", pw);

        Assert.All(pw, c => Assert.Equal('\0', c)); // buffer zerado após guardar no cofre
    }

    [Fact]
    public async Task Delete_RevokesEnvelope_AndRemovesRef_ForInlineCred()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var svc = new InlineCredentialService(store, vault);
        string credId = await svc.CreateForEndpointAsync("ws-local", "ep-1", "admin", "pw".ToCharArray());
        var ep = new Endpoint { Id = "ep-1", AssetId = "a", Protocol = "ssh", CredentialRefId = credId };

        await svc.DeleteForEndpointAsync(ep);

        Assert.Null(await store.GetCredentialRefAsync(credId));
        Assert.Contains("env-" + credId, vault.RevokedEnvelopeIds);
    }

    [Fact]
    public async Task Delete_DoesNotTouch_KeychainSharedCred()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var svc = new InlineCredentialService(store, vault);
        // Credencial do Keychain (escopo null = compartilhada) apontada por um endpoint.
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "shared-1",
            Name = "Login padrão",
            Type = CredentialTypes.Password,
            Scope = null,
            SecretEnvelopeId = "env-shared-1",
        });
        var ep = new Endpoint { Id = "ep-2", AssetId = "a", Protocol = "ssh", CredentialRefId = "shared-1" };

        await svc.DeleteForEndpointAsync(ep);

        Assert.NotNull(await store.GetCredentialRefAsync("shared-1")); // NÃO apagada
        Assert.Empty(vault.RevokedEnvelopeIds);
    }

    [Fact]
    public async Task HostEditor_InlineEndpoint_Save_CreatesHiddenCred_AndAttachesToEndpoint()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var svc = new InlineCredentialService(store, vault);
        var vm = new HostEditorViewModel(store, "ws-local", existing: null, groupId: null, inlineCreds: svc);

        vm.Name = "OLT-1";
        vm.UseInlineCredential = true;
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.9";
        vm.NewEndpointInlineUsername = "root";
        vm.AddInlineEndpoint("s3nha".ToCharArray());

        await vm.SaveAsync();

        var asset = Assert.Single(await store.GetAssetsAsync("ws-local"));
        var ep = Assert.Single(asset.Endpoints);
        Assert.NotNull(ep.CredentialRefId);

        var cred = await store.GetCredentialRefAsync(ep.CredentialRefId!);
        Assert.Equal("root", cred!.Metadata?.Username);
        Assert.Equal($"endpoint:{ep.Id}", cred.Scope);
        Assert.Contains(cred.Id, vault.StoredCredentialIds);

        // Escondida do Keychain/dropdown.
        var list = await store.GetCredentialRefsAsync("ws-local");
        Assert.DoesNotContain(list, c => c.Id == cred.Id);
    }

    [Fact]
    public void CanAddEndpoint_KeychainMode_RequiresOnlyAddress()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);
        vm.NewEndpointAddress = "10.0.0.1";
        Assert.True(vm.CanAddEndpoint);
    }

    [Fact]
    public void CanAddEndpoint_InlineMode_RequiresAddressUsernameAndPassword()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);
        vm.UseInlineCredential = true;

        vm.NewEndpointAddress = "10.0.0.1";
        Assert.False(vm.CanAddEndpoint);   // sem usuário nem senha
        vm.NewEndpointInlineUsername = "root";
        Assert.False(vm.CanAddEndpoint);   // sem senha
        vm.HasInlinePassword = true;
        Assert.True(vm.CanAddEndpoint);    // endereço + usuário + senha → ok

        vm.HasInlinePassword = false;
        Assert.False(vm.CanAddEndpoint);   // tirou a senha → bloqueia de novo
    }

    [Fact]
    public async Task HostEditor_RemovingInlineEndpoint_OnEdit_DeletesItsCred()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var svc = new InlineCredentialService(store, vault);

        // Cria um device com endpoint + credencial inline.
        var create = new HostEditorViewModel(store, "ws-local", existing: null, groupId: null, inlineCreds: svc);
        create.Name = "Sw";
        create.UseInlineCredential = true;
        create.NewEndpointAddress = "10.0.0.1";
        create.NewEndpointInlineUsername = "adm";
        create.AddInlineEndpoint("pw".ToCharArray());
        await create.SaveAsync();

        var asset = Assert.Single(await store.GetAssetsAsync("ws-local"));
        string credId = asset.Endpoints[0].CredentialRefId!;

        // Reabre em edição, remove o endpoint e salva → a credencial inline é apagada (cascata).
        var edit = new HostEditorViewModel(store, "ws-local", existing: asset, groupId: null, inlineCreds: svc);
        edit.RemoveEndpointCommand.Execute(asset.Endpoints[0]);
        await edit.SaveAsync();

        Assert.Null(await store.GetCredentialRefAsync(credId));
        Assert.Contains("env-" + credId, vault.RevokedEnvelopeIds);
    }
}
