using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

/// <summary>
/// A FIAÇÃO, que é onde mora o cenário "os 700 sumiram". Aqui não se testa cripto: testa-se se o
/// escopo da sessão chega, inteiro e correto, em cada ponto que grava.
///
/// <para><b>Os dois eixos que precisam ficar SEPARADOS:</b> o escopo do STORE
/// (<c>assets.workspace_id</c>, que dono e colega precisam compartilhar com a MESMA string dentro do
/// banco do time) e a identidade do COFRE (<c>ws-local</c> vs <c>time:{W}</c>, que precisa ser
/// diferente, senão as senhas do cliente e as do operador se misturam). Antes desta fatia os dois
/// eram a mesma variável.</para>
/// </summary>
public sealed class SessionWiringTests
{
    private const string ServerWorkspace = "8f3b6f4a-0000-4000-8000-000000000001";

    private static byte[] Amk() => RandomNumberGenerator.GetBytes(32);

    private sealed record Montagem(ServiceProvider Sp, InMemoryCredentialStore Envelopes, EspiaoDeEscopo Store);

    /// <summary>
    /// Store que ANOTA com qual escopo cada consulta de credencial foi feita, delegando o resto. A
    /// asserção precisa ser sobre a CHAMADA, e não sobre o resultado: um duplo em memória cujo filtro
    /// é frouxo faria um teste de resultado passar mesmo com o escopo errado — e o escopo errado é
    /// justamente o que esvazia a lista do colega no banco do time, em silêncio.
    /// </summary>
    private sealed class EspiaoDeEscopo : ILocalStore
    {
        private readonly ILocalStore _inner;

        public EspiaoDeEscopo(ILocalStore inner) => _inner = inner;

        public List<string> EscoposConsultados { get; } = [];

        public Task DeleteGroupAsync(string id, CancellationToken ct = default)
            => _inner.DeleteGroupAsync(id, ct);

        public Task<IReadOnlyList<AssetGroup>> GetGroupsAsync(string workspaceId, CancellationToken ct = default)
            => _inner.GetGroupsAsync(workspaceId, ct);

        public Task<AssetGroup> AddGroupAsync(string workspaceId, string name, string? parentId = null, CancellationToken ct = default)
            => _inner.AddGroupAsync(workspaceId, name, parentId, ct);

        public Task RenameGroupAsync(string id, string newName, CancellationToken ct = default)
            => _inner.RenameGroupAsync(id, newName, ct);

        public Task<AssetGroup> UpdateGroupAsync(AssetGroup group, CancellationToken ct = default)
            => _inner.UpdateGroupAsync(group, ct);

        public Task<IReadOnlyList<Asset>> GetAssetsAsync(string workspaceId, string? groupId = null, CancellationToken ct = default)
            => _inner.GetAssetsAsync(workspaceId, groupId, ct);

        public Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default)
            => _inner.GetAssetAsync(id, ct);

        public Task<Asset> AddAssetAsync(AddAssetRequest request, CancellationToken ct = default)
            => _inner.AddAssetAsync(request, ct);

        public Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default)
            => _inner.UpdateAssetAsync(asset, ct);

        public Task DeleteAssetAsync(string id, CancellationToken ct = default)
            => _inner.DeleteAssetAsync(id, ct);

        public Task<Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default)
            => _inner.GetEndpointAsync(endpointId, ct);

        public Task<Endpoint> AddEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => _inner.AddEndpointAsync(endpoint, ct);

        public Task<Endpoint> UpdateEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => _inner.UpdateEndpointAsync(endpoint, ct);

        public Task DeleteEndpointAsync(string id, CancellationToken ct = default)
            => _inner.DeleteEndpointAsync(id, ct);

        public Task<IReadOnlyList<CredentialRef>> GetCredentialRefsAsync(string workspaceId, CancellationToken ct = default)
        {
            EscoposConsultados.Add(workspaceId);
            return _inner.GetCredentialRefsAsync(workspaceId, ct);
        }

        public Task<CredentialRef?> GetCredentialRefAsync(string credentialRefId, CancellationToken ct = default)
            => _inner.GetCredentialRefAsync(credentialRefId, ct);

        public Task<CredentialRef> AddCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
            => _inner.AddCredentialRefAsync(credentialRef, ct);

        public Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
            => _inner.UpdateCredentialRefAsync(credentialRef, ct);

        public Task DeleteCredentialRefAsync(string id, CancellationToken ct = default)
            => _inner.DeleteCredentialRefAsync(id, ct);
    }

    private static Montagem BuildFor(SessionVaultScope scope, IWorkspaceKeyRing ring)
    {
        var envelopes = new InMemoryCredentialStore();
        var vault = new CredentialVault(envelopes, ring, NullVaultAuditSink.Instance);
        var store = new EspiaoDeEscopo(new InMemoryLocalStore());
        return new Montagem(AppCompositionRoot.Build(vault, store, scope), envelopes, store);
    }

    private static async Task<SecretEnvelope> GuardaSenhaPeloKeychainAsync(ServiceProvider sp, InMemoryCredentialStore envelopes)
    {
        var keychain = sp.GetRequiredService<KeychainViewModel>();
        await keychain.CreateAsync("Roteador do cliente", "admin", "senha-do-cliente".ToCharArray());

        CredentialRef gravada = Assert.Single(keychain.Credentials);
        SecretEnvelope? envelope = await envelopes.GetAsync(gravada.SecretEnvelopeId!);
        Assert.NotNull(envelope);
        return envelope;
    }

    /// <summary>
    /// <b>Sessão de TIME: a senha nasce sob a WK e no workspace do time.</b> As duas asserções são
    /// necessárias — carimbo certo com workspace errado grava a senha do cliente no cofre pessoal, e
    /// workspace certo com carimbo errado grava um envelope que monta o AAD errado e nunca abre. A
    /// fiação meio-feita morre aqui.
    /// </summary>
    [Fact]
    public async Task SessaoDeTime_GravaSenhaComoWkRootedV1_ENoWorkspaceDoTime()
    {
        byte[] amk = Amk();
        var keyStore = new InMemoryWorkspaceKeyStore();
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(keyStore, amk);
        using var amkRing = new AmkWorkspaceKeyRing(amk);

        SessionVaultScope scope = SessionVaultScope.Team(ServerWorkspace, temChave: true);
        (await teamRing.MintWorkspaceKeyAsync(scope.VaultWorkspaceId)).Dispose();

        Montagem m = BuildFor(
            scope, new RoutedWorkspaceKeyRing(amkRing, [(AppRuntime.TeamVaultPrefix, teamRing)]));
        using ServiceProvider _ = m.Sp;

        SecretEnvelope envelope = await GuardaSenhaPeloKeychainAsync(m.Sp, m.Envelopes);

        Assert.Equal(VaultAlgorithms.WkRootedV1, envelope.Algorithm);
        Assert.Equal("time:" + ServerWorkspace, envelope.WorkspaceId);
    }

    /// <summary>
    /// <b>O escopo do STORE continua <c>ws-local</c> nos DOIS bancos.</b> O <c>workspace_id</c> das
    /// entidades viaja dentro do banco e é consultado com <c>WHERE workspace_id = $wid</c>: dono e
    /// colega precisam calcular a MESMA string, senão a lista do outro fica VAZIA — em silêncio, que
    /// é a falha desta base. <c>"ws-local"</c> já é a constante em todo cliente; derivá-la do
    /// workspace do servidor seria uma falha silenciosa nova de graça.
    /// </summary>
    [Fact]
    public async Task SessaoDeTime_MantemOEscopoDoStoreEmWsLocal()
    {
        byte[] amk = Amk();
        var keyStore = new InMemoryWorkspaceKeyStore();
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(keyStore, amk);
        using var amkRing = new AmkWorkspaceKeyRing(amk);

        SessionVaultScope scope = SessionVaultScope.Team(ServerWorkspace, temChave: true);
        (await teamRing.MintWorkspaceKeyAsync(scope.VaultWorkspaceId)).Dispose();

        Montagem m = BuildFor(
            scope, new RoutedWorkspaceKeyRing(amkRing, [(AppRuntime.TeamVaultPrefix, teamRing)]));
        using ServiceProvider _ = m.Sp;
        EspiaoDeEscopo espiao = m.Store;

        await GuardaSenhaPeloKeychainAsync(m.Sp, m.Envelopes);

        // O Keychain CONSULTA o store com "ws-local" — a mesma string que o colega calcula dentro do
        // banco do time. Afirmado sobre a chamada, e não sobre o resultado: um duplo em memória que
        // ignorasse o filtro faria um teste de resultado passar mesmo com o escopo errado.
        var keychain = m.Sp.GetRequiredService<KeychainViewModel>();
        await keychain.LoadAsync();

        Assert.Equal(["ws-local", "ws-local"], espiao.EscoposConsultados);
        Assert.Single(keychain.Credentials);
    }

    /// <summary>
    /// <b>O simétrico, e o guarda dos ~700:</b> a sessão pessoal continua IDÊNTICA à de hoje —
    /// <c>ws-local</c> no cofre, <c>AmkRootedV1</c> no carimbo, <c>ws-local</c> no store. Nenhum
    /// envelope do operador muda de lugar nem de raiz por causa desta fatia.
    /// </summary>
    [Fact]
    public async Task SessaoPessoal_ContinuaIdentica_AmkRootedV1_EWsLocal()
    {
        byte[] amk = Amk();
        using var amkRing = new AmkWorkspaceKeyRing(amk);
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(amk);

        Montagem m = BuildFor(
            SessionVaultScope.Personal,
            new RoutedWorkspaceKeyRing(amkRing, [(AppRuntime.TeamVaultPrefix, teamRing)]));
        using ServiceProvider _ = m.Sp;

        SecretEnvelope envelope = await GuardaSenhaPeloKeychainAsync(m.Sp, m.Envelopes);

        Assert.Equal(VaultAlgorithms.AmkRootedV1, envelope.Algorithm);
        Assert.Equal("ws-local", envelope.WorkspaceId);

        var store = m.Sp.GetRequiredService<ILocalStore>();
        Assert.Single(await store.GetCredentialRefsAsync("ws-local"));
    }

    /// <summary>
    /// A senha INLINE (presa a um endpoint) segue o mesmo escopo de cofre da sessão. Ela era o outro
    /// ponto que recebia o workspace por parâmetro do chamador — duas fontes para a mesma verdade,
    /// que é como um lado acaba gravando no cofre errado enquanto o outro acerta.
    /// </summary>
    [Fact]
    public async Task SenhaInline_SegueOMesmoCofreDaSessao()
    {
        byte[] amk = Amk();
        var keyStore = new InMemoryWorkspaceKeyStore();
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(keyStore, amk);
        using var amkRing = new AmkWorkspaceKeyRing(amk);

        SessionVaultScope scope = SessionVaultScope.Team(ServerWorkspace, temChave: true);
        (await teamRing.MintWorkspaceKeyAsync(scope.VaultWorkspaceId)).Dispose();

        Montagem m = BuildFor(
            scope, new RoutedWorkspaceKeyRing(amkRing, [(AppRuntime.TeamVaultPrefix, teamRing)]));
        using ServiceProvider _ = m.Sp;

        var inline = m.Sp.GetRequiredService<RemoteOps.Desktop.Credentials.IInlineCredentialService>();
        string credId = await inline.CreateForEndpointAsync(
            "endpoint-1", "admin", "senha-inline".ToCharArray());

        var store = m.Sp.GetRequiredService<ILocalStore>();
        CredentialRef? gravada = await store.GetCredentialRefAsync(credId);
        Assert.NotNull(gravada);

        SecretEnvelope? envelope = await m.Envelopes.GetAsync(gravada.SecretEnvelopeId!);
        Assert.NotNull(envelope);
        Assert.Equal("time:" + ServerWorkspace, envelope.WorkspaceId);
        Assert.Equal(VaultAlgorithms.WkRootedV1, envelope.Algorithm);
    }
}
