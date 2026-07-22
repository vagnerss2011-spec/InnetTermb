using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// A RAIZ das senhas viaja com a sessão de sync — e o que acontece quando o fio não a informa.
///
/// <para><b>O buraco:</b> o <c>SecretSyncOrchestrator</c> sempre aceitou um <c>vaultAlgorithm</c>, e
/// o <c>SyncSessionFactory</c> simplesmente não o informava. O valor certo existia e não chegava a
/// quem precisava dele — então, num cofre de TIME, um registro sem <c>algorithm</c> no fio seria
/// gravado como <c>AmkRootedV1</c>: um envelope que monta o AAD errado e nunca abre, sem erro na
/// hora em que o defeito é cometido.</para>
/// </summary>
public sealed class SecretSyncAlgorithmTests
{
    private const string ServerWorkspace = "8f3b6f4a-0000-4000-8000-000000000001";
    private const string TeamVault = "time:8f3b6f4a-0000-4000-8000-000000000001";

    private static SecretEnvelopeDto Dto(string id, string? algorithm) => new(
        Id: id,
        WorkspaceId: ServerWorkspace,
        Ciphertext: Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        Nonce: Convert.ToBase64String(new byte[12]),
        Tag: Convert.ToBase64String(new byte[16]),
        WrappedCek: Convert.ToBase64String(new byte[32]),
        CekNonce: Convert.ToBase64String(new byte[12]),
        CekTag: Convert.ToBase64String(new byte[16]),
        KeyVersion: "1|password|cred-01",
        Version: 1)
    {
        Algorithm = algorithm,
    };

    private static SecretSyncOrchestrator Orchestrator(
        FakeSecretsApi api, IVaultMigrationStore store, string vaultAlgorithm) =>
        new(ServerWorkspace, TeamVault, store, api, new FakeSyncMetadataStore(),
            amkKeyVersion: 1, pageSize: 50, vaultAlgorithm);

    /// <summary>Cofre em arquivo, como nos demais testes de sync — o store real, não um dicionário.</summary>
    private static FileVaultStore NewStore() => new(Path.Combine(
        Path.GetTempPath(), "remoteops-algo-" + Guid.NewGuid().ToString("n"), "vault.json"));

    /// <summary>
    /// <b>DTO sem <c>algorithm</c>, numa sessão de TIME, é gravado como <c>WkRootedV1</c>.</b> Sem
    /// este teste, o default silencioso (<c>AmkRootedV1</c>) volta no primeiro refactor — e volta
    /// calado, porque nada na tela distingue um envelope selado da raiz certa de um selado da errada
    /// até alguém tentar abri-lo.
    /// </summary>
    [Fact]
    public async Task DtoSemAlgorithm_EmSessaoDeTime_GravaWkRootedV1()
    {
        var api = new FakeSecretsApi();
        var store = NewStore();
        api.SeedRawWithoutAlgorithm(Dto("11111111-1111-4111-8111-111111111111", algorithm: null));

        await Orchestrator(api, store, VaultAlgorithms.WkRootedV1).SyncOnceAsync();

        SecretEnvelope gravado = Assert.Single(await store.ListEnvelopesAsync(TeamVault));
        Assert.Equal(VaultAlgorithms.WkRootedV1, gravado.Algorithm);
        Assert.Equal(TeamVault, gravado.WorkspaceId);
    }

    /// <summary>
    /// O simétrico, e o guarda do cofre pessoal: a MESMA descida, numa sessão pessoal, continua
    /// gravando <c>AmkRootedV1</c>. Nada do acervo do operador muda por causa desta fatia.
    /// </summary>
    [Fact]
    public async Task DtoSemAlgorithm_EmSessaoPessoal_ContinuaAmkRootedV1()
    {
        var api = new FakeSecretsApi();
        var store = NewStore();
        api.SeedRawWithoutAlgorithm(Dto("22222222-2222-4222-8222-222222222222", algorithm: null));

        await new SecretSyncOrchestrator(
            ServerWorkspace, "ws-local", store, api, new FakeSyncMetadataStore()).SyncOnceAsync();

        SecretEnvelope gravado = Assert.Single(await store.ListEnvelopesAsync("ws-local"));
        Assert.Equal(VaultAlgorithms.AmkRootedV1, gravado.Algorithm);
    }

    /// <summary>
    /// <b>Envelope de raiz DIVERGENTE não é gravado — vai para o Skipped.</b> Numa sessão de cofre de
    /// TIME, um dto carimbado com outra raiz é veneno: gravá-lo produziria um envelope que ninguém
    /// abre e que <b>parece</b> estar lá. Melhor não ter a senha e SABER do que tê-la ilegível e não
    /// saber — e é por isso que o estágio seguinte torna o <c>Skipped</c> visível na barra.
    ///
    /// <para>A guarda vale só no cofre do time: no pessoal (AMK) o comportamento fica idêntico ao de
    /// hoje, para não barrar envelopes legados que ainda carreguem o carimbo do DPAPI.</para>
    /// </summary>
    [Fact]
    public async Task EnvelopeDeRaizDivergente_VaiParaSkipped_NaoEGravado()
    {
        var api = new FakeSecretsApi();
        var store = NewStore();
        api.SeedRaw(Dto("33333333-3333-4333-8333-333333333333", VaultAlgorithms.AmkRootedV1));

        SecretSyncReport report = await Orchestrator(api, store, VaultAlgorithms.WkRootedV1).SyncOnceAsync();

        Assert.Empty(await store.ListEnvelopesAsync(TeamVault));
        SecretSyncSkip pulado = Assert.Single(report.Skipped);
        Assert.Equal(SecretSyncPhase.Pull, pulado.Phase);
    }

    /// <summary>
    /// E no cofre PESSOAL a mesma descida passa: a guarda não pode virar um filtro que barra o
    /// acervo legado do operador — ele tem envelopes carimbados <c>DpapiRootedV1</c> em disco.
    /// </summary>
    [Fact]
    public async Task NoCofrePessoal_CarimboDiferente_CONTINUA_Passando()
    {
        var api = new FakeSecretsApi();
        var store = NewStore();
        api.SeedRaw(Dto("44444444-4444-4444-8444-444444444444", VaultAlgorithms.DpapiRootedV1));

        SecretSyncReport report = await new SecretSyncOrchestrator(
            ServerWorkspace, "ws-local", store, api, new FakeSyncMetadataStore()).SyncOnceAsync();

        Assert.Empty(report.Skipped);
        Assert.Single(await store.ListEnvelopesAsync("ws-local"));
    }
}
