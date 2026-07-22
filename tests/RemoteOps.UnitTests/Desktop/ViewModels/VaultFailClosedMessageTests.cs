using System;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// <b>A recusa do fail-closed chegando ONDE o operador está.</b>
///
/// <para>Num cofre de time sem a chave, o chaveiro recusa ALTO — de propósito — com uma
/// <see cref="VaultException"/> cuja mensagem já é acionável em pt-BR ("aceite o convite … antes de
/// cadastrar ou abrir senhas do time"). Só que ninguém a capturava: no Keychain ela subia até o
/// <c>DispatcherUnhandledException</c> e virava uma caixa intitulada <i>"Erro inesperado"</i>, que
/// esconde a frase útil atrás da moldura errada; no editor de host era pior, porque o
/// <c>SaveCommand</c> descarta a Task (<c>_ = SaveAsync()</c>) — a exceção nunca era observada e o
/// clique em "Salvar" simplesmente <b>não fazia nada</b>, sem uma linha na tela.</para>
///
/// <para>A guarda continua idêntica: o que muda é que a recusa vira TEXTO no lugar em que o operador
/// está olhando, preservando a mensagem que o cofre escreveu.</para>
/// </summary>
public sealed class VaultFailClosedMessageTests
{
    /// <summary>A frase REAL do fail-closed do <c>WkWorkspaceKeyRing</c>, encurtada.</summary>
    private const string RecusaDoCofre =
        "O cofre do time 'time:W' ainda não tem a chave neste computador. Aceite o convite "
        + "(identificador + código recebido por outro canal) antes de cadastrar ou abrir senhas "
        + "do time.";

    /// <summary>Cofre que recusa como o do time sem chave: toda escrita estoura fail-closed.</summary>
    private sealed class FailClosedVault : IVault
    {
        public Task<SecretEnvelope> StoreAsync(
            VaultStoreRequest r, ReadOnlyMemory<char> secret, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);

        public Task<VaultSecret> RetrieveAsync(
            string envelopeId, VaultAccessContext c, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);

        public Task<SecretEnvelope> RotateAsync(
            string envelopeId, ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);

        public Task RevokeAsync(
            string envelopeId, VaultAccessContext c, CancellationToken ct = default)
            => throw new VaultException(RecusaDoCofre);
    }

    private sealed class FailClosedInlineCredentials : IInlineCredentialService
    {
        public Task<string> CreateForEndpointAsync(
            string endpointId, string username, char[] password, CancellationToken ct = default)
        {
            Array.Clear(password);
            throw new VaultException(RecusaDoCofre);
        }

        public Task DeleteForEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => Task.CompletedTask;

        public bool IsInlineScope(string? scope) => scope?.StartsWith("endpoint:", StringComparison.Ordinal) == true;
    }

    // ── Keychain ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cadastrar senha no cofre do time sem a chave: o VM NÃO deixa a exceção subir (ela viraria a
    /// caixa de "erro inesperado") e a frase do cofre fica na tela, inteira.
    /// </summary>
    [Fact]
    public async Task Keychain_CriarSenhaSemAChaveDoTime_MOSTRA_AFrase_EmVezDeEstourar()
    {
        var vm = new KeychainViewModel(
            new InMemoryLocalStore(), new FailClosedVault(), "ws-local", "time:W");

        await vm.CreateAsync("Cliente ACME", "admin", "segredo".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Contains(RecusaDoCofre, vm.ErrorMessage, StringComparison.Ordinal);

        // E nada de meia-gravação: sem envelope, não entra CredentialRef apontando para o vazio.
        Assert.Empty(vm.Credentials);
    }

    /// <summary>Trocar a senha (rotação) recusa do mesmo jeito, e diz o mesmo.</summary>
    [Fact]
    public async Task Keychain_TrocarSenhaSemAChaveDoTime_MOSTRA_AFrase()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "c1",
            Name = "root",
            Type = CredentialTypes.Password,
            SecretEnvelopeId = "e1",
        });

        var vm = new KeychainViewModel(store, new FailClosedVault(), "ws-local", "time:W");
        await vm.LoadAsync();

        await vm.ChangePasswordAsync(vm.Credentials[0], "nova".ToCharArray());

        Assert.True(vm.HasError);
        Assert.Contains(RecusaDoCofre, vm.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>Excluir também passa pelo cofre (revoga o envelope) — e também recusa com texto.</summary>
    [Fact]
    public async Task Keychain_ExcluirSemAChaveDoTime_MOSTRA_AFrase()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "c1",
            Name = "root",
            Type = CredentialTypes.Password,
            SecretEnvelopeId = "e1",
        });

        var vm = new KeychainViewModel(store, new FailClosedVault(), "ws-local", "time:W");
        await vm.LoadAsync();

        await vm.DeleteAsync(vm.Credentials[0]);

        Assert.True(vm.HasError);
        Assert.Contains(RecusaDoCofre, vm.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A metade que impede "recusar sempre".</b> Com o cofre funcionando, a operação passa e a
    /// tela não fica com um erro velho colado nela — recado que não some é indistinguível de recado
    /// novo, e é assim que o operador aprende a ignorá-los.
    /// </summary>
    [Fact]
    public async Task Keychain_ComOCofreFUNCIONANDO_Grava_ELimpaOErroAnterior()
    {
        var store = new InMemoryLocalStore();
        var falho = new KeychainViewModel(store, new FailClosedVault(), "ws-local", "time:W");
        await falho.CreateAsync("Cliente ACME", "admin", "segredo".ToCharArray());
        Assert.True(falho.HasError);

        var ok = new KeychainViewModel(store, new FakeVault(), "ws-local", "ws-local");
        await ok.CreateAsync("Cliente ACME", "admin", "segredo".ToCharArray());

        Assert.False(ok.HasError);
        Assert.Equal(string.Empty, ok.ErrorMessage);
        Assert.Single(ok.Credentials);
    }

    // ── Editor de host (senha inline) ────────────────────────────────────────────────────

    /// <summary>
    /// <b>O pior dos dois casos, porque era MUDO.</b> O <c>SaveCommand</c> faz
    /// <c>_ = SaveAsync()</c>: a <see cref="VaultException"/> ficava dentro de uma Task que ninguém
    /// observa, então o clique em "Salvar" não salvava, não fechava e não dizia nada. Agora a recusa
    /// vira texto no diálogo — e o <c>Saved</c> NÃO dispara, senão a janela fecharia por cima de um
    /// cadastro que não aconteceu.
    /// </summary>
    [Fact]
    public async Task EditorDeHost_SenhaInlineSemAChaveDoTime_MOSTRA_AFrase_ENaoFechaODialogo()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostEditorViewModel(
            store, "ws-local", existing: null, groupId: null, new FailClosedInlineCredentials());

        vm.Name = "Cliente ACME";
        vm.UseInlineCredential = true;
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointPort = 22;
        vm.NewEndpointInlineUsername = "admin";
        vm.AddInlineEndpoint("segredo".ToCharArray());

        bool fechou = false;
        vm.Saved += (_, _) => fechou = true;

        await vm.SaveAsync();

        Assert.True(vm.HasError);
        Assert.Contains(RecusaDoCofre, vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(fechou);

        // ⚠️ E o endereço saiu da lista. O rascunho já foi consumido e a senha, zerada pelo serviço
        // (o certo). Se ele ficasse ali, um SEGUNDO clique em "Salvar" o gravaria SEM credencial
        // nenhuma, em silêncio — a recusa alta do cofre viraria um equipamento cadastrado sem senha.
        Assert.Empty(vm.Endpoints);
    }

    /// <summary>
    /// A metade que impede "recusar sempre": sem senha inline no meio, salvar continua salvando e
    /// fechando o diálogo, exatamente como antes.
    /// </summary>
    [Fact]
    public async Task EditorDeHost_SemSenhaInline_CONTINUA_SalvandoEFechando()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostEditorViewModel(
            store, "ws-local", existing: null, groupId: null, new FailClosedInlineCredentials());

        vm.Name = "Cliente ACME";
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointPort = 22;
        vm.AddEndpointCommand.Execute(null);

        bool fechou = false;
        vm.Saved += (_, _) => fechou = true;

        await vm.SaveAsync();

        Assert.False(vm.HasError);
        Assert.True(fechou);
    }
}
