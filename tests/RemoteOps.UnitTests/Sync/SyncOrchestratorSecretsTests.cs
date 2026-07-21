using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

using RemoteOps.Contracts.Sync;
using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// A composição das duas metades do ciclo: metadados (changelog) + segredos (canal próprio). O
/// <see cref="SecretSyncOrchestrator"/> é uma classe separada, mas roda DENTRO do ciclo do
/// <see cref="SyncOrchestrator"/> — estes testes prendem essa decisão.
/// </summary>
public sealed class SyncOrchestratorSecretsTests : IDisposable
{
    private const string VaultWorkspace = "ws-local";
    private static readonly string ServerWorkspace = Guid.NewGuid().ToString();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"remoteops-orch-sec-{Guid.NewGuid():n}");
    private readonly AmkWorkspaceKeyRing _keyRing = new(RandomNumberGenerator.GetBytes(32));

    public void Dispose()
    {
        _keyRing.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private (FileVaultStore Store, CredentialVault Vault) Vault()
    {
        var store = new FileVaultStore(Path.Combine(_dir, "vault.json"));
        return (store, new CredentialVault(store, _keyRing, NullVaultAuditSink.Instance));
    }

    /// <summary>
    /// Sem canal de segredos, o ciclo é exatamente o de antes da Fase 1 — nada de novo acontece.
    /// (Compatibilidade: é o caminho de quem não tem conta E2EE.)
    /// </summary>
    [Fact]
    public async Task SemCanalDeSegredos_OCicloSegueSoDeMetadados()
    {
        var orch = new SyncOrchestrator(
            ServerWorkspace, new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        await orch.SyncOnceAsync();

        Assert.Equal(SyncState.Synced, orch.Status.State);

        // Sem canal não há o que reportar: nem saudável nem quebrado — ocioso.
        Assert.Equal(SecretChannelState.Idle, orch.Status.SecretChannel);
    }

    /// <summary>
    /// Com canal, o ciclo do <see cref="SyncOrchestrator"/> também move os envelopes — sem ninguém
    /// precisar chamar o canal de segredos por fora.
    /// </summary>
    [Fact]
    public async Task ComCanalDeSegredos_OCicloTambemSincronizaEnvelopes()
    {
        (FileVaultStore store, CredentialVault vault) = Vault();
        await vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspace,
                CredentialId = "c1",
                Type = "password",
                ActorUserId = "op",
            },
            "senha-do-host".AsMemory());

        var secretsApi = new FakeSecretsApi();
        secretsApi.Forbid("senha-do-host");
        var metadata = new FakeSyncMetadataStore();
        var secrets = new SecretSyncOrchestrator(
            ServerWorkspace, VaultWorkspace, store, secretsApi, metadata);

        var orch = new SyncOrchestrator(
            ServerWorkspace, new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), metadata, pageSize: 200, secrets: secrets);

        await orch.SyncOnceAsync();

        Assert.Single(secretsApi.Accepted);
        Assert.Equal(SyncState.Synced, orch.Status.State);
    }

    /// <summary>
    /// <b>Ordem.</b> Os metadados vêm primeiro: o <c>credential_ref</c> tem que estar aplicado antes
    /// de o envelope dele chegar, senão o device que recebe fica com uma senha órfã de credencial.
    /// </summary>
    [Fact]
    public async Task MetadadosSaoAplicadosAntesDosSegredos()
    {
        (FileVaultStore store, CredentialVault vault) = Vault();
        await vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspace,
                CredentialId = "c1",
                Type = "password",
                ActorUserId = "op",
            },
            "senha".AsMemory());

        var order = new List<string>();
        var applier = new RecordingApplier(order);
        var secretsApi = new RecordingSecretsApi(order);

        var api = new FakeCloudSyncApi();
        api.PullResponses.Enqueue(new PullResponse(
            [new SyncChange { EntityType = "credential_ref", EntityId = "c1", Operation = "created", Patch = [] }],
            1, false));

        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator(
            ServerWorkspace, new FakeOutbox(), api, applier, metadata, pageSize: 200,
            secrets: new SecretSyncOrchestrator(ServerWorkspace, VaultWorkspace, store, secretsApi, metadata));

        await orch.SyncOnceAsync();

        Assert.Equal(["metadados", "segredos"], order);
    }

    /// <summary>
    /// <b>A falha do canal de segredos tem estado PRÓPRIO.</b> Não relança (offline-first, ADR-002) e,
    /// principalmente, não vira o <see cref="SyncState.Error"/> genérico do changelog: os metadados
    /// FORAM, e dizer "Erro de sincronização" esconderia justamente a informação que importa — a de
    /// que as SENHAS não estão subindo. Foi essa fusão que deixou o operador com "Sincronizado" na
    /// tela enquanto nenhuma credencial sincronizava.
    /// </summary>
    [Fact]
    public async Task FalhaNoCanalDeSegredos_EhReportadaSeparadaDoChangelog()
    {
        (FileVaultStore store, CredentialVault vault) = Vault();
        await vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspace,
                CredentialId = "c1",
                Type = "password",
                ActorUserId = "op",
            },
            "senha".AsMemory());

        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator(
            ServerWorkspace, new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), metadata, pageSize: 200,
            secrets: new SecretSyncOrchestrator(
                ServerWorkspace, VaultWorkspace, store, new ExplodingSecretsApi(), metadata));

        await orch.SyncOnceAsync(); // não relança

        Assert.Equal(SyncState.Synced, orch.Status.State); // o changelog passou
        Assert.Equal(SecretChannelState.Failed, orch.Status.SecretChannel);
    }

    /// <summary>
    /// Ciclo limpo reporta o canal SAUDÁVEL. Sem isto, "Degradado" seria vácuo — todo ciclo poderia
    /// estar dizendo a mesma coisa.
    /// </summary>
    [Fact]
    public async Task CicloLimpo_ReportaOCanalSaudavel()
    {
        (FileVaultStore store, CredentialVault vault) = Vault();
        await vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspace,
                CredentialId = "c1",
                Type = "password",
                ActorUserId = "op",
            },
            "senha".AsMemory());

        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator(
            ServerWorkspace, new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), metadata, pageSize: 200,
            secrets: new SecretSyncOrchestrator(
                ServerWorkspace, VaultWorkspace, store, new FakeSecretsApi(), metadata));

        await orch.SyncOnceAsync();

        Assert.Equal(SyncState.Synced, orch.Status.State);
        Assert.Equal(SecretChannelState.Healthy, orch.Status.SecretChannel);
    }

    /// <summary>
    /// Item PULADO não é ciclo limpo. O envelope malformado não trava mais nada (ver
    /// <see cref="SecretChannelResilienceTests"/>), mas o operador precisa saber que uma senha ficou
    /// para trás — senão trocamos um travamento silencioso por uma perda silenciosa.
    /// </summary>
    [Fact]
    public async Task ItemPuladoNoCanalDeSegredos_ViraCanalDegradado()
    {
        (FileVaultStore store, CredentialVault vault) = Vault();
        SecretEnvelope sadio = await vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspace,
                CredentialId = "c1",
                Type = "password",
                ActorUserId = "op",
            },
            "senha".AsMemory());

        // Envelope com id fora do formato GUID: o codec recusa, o item é pulado, o ciclo segue.
        await store.SaveAsync(sadio with { EnvelopeId = "nao-e-um-guid", CredentialId = "c2" });

        var metadata = new FakeSyncMetadataStore();
        var orch = new SyncOrchestrator(
            ServerWorkspace, new FakeOutbox(), new FakeCloudSyncApi(),
            new FakeRemoteChangeApplier(), metadata, pageSize: 200,
            secrets: new SecretSyncOrchestrator(
                ServerWorkspace, VaultWorkspace, store, new FakeSecretsApi(), metadata));

        await orch.SyncOnceAsync();

        Assert.Equal(SyncState.Synced, orch.Status.State);
        Assert.Equal(SecretChannelState.Degraded, orch.Status.SecretChannel);
    }

    private sealed class RecordingApplier(List<string> order) : IRemoteChangeApplier
    {
        public Task<int> ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default)
        {
            order.Add("metadados");
            return Task.FromResult(changes.Count);
        }
    }

    private sealed class RecordingSecretsApi(List<string> order) : ISecretsApi
    {
        public Task<IReadOnlyList<SecretUpsertResult>> PushAsync(
            string workspaceId, IReadOnlyList<SecretEnvelopeDto> envelopes, CancellationToken ct = default)
        {
            order.Add("segredos");
            return Task.FromResult<IReadOnlyList<SecretUpsertResult>>(
                [new SecretUpsertResult("ok", 1, 1)]);
        }

        public Task<SecretsPullResponse> PullAsync(
            string workspaceId, long since, int pageSize, CancellationToken ct = default)
            => Task.FromResult(new SecretsPullResponse([], since, false));
    }

    private sealed class ExplodingSecretsApi : ISecretsApi
    {
        public Task<IReadOnlyList<SecretUpsertResult>> PushAsync(
            string workspaceId, IReadOnlyList<SecretEnvelopeDto> envelopes, CancellationToken ct = default)
            => throw new HttpRequestException("servidor fora");

        public Task<SecretsPullResponse> PullAsync(
            string workspaceId, long since, int pageSize, CancellationToken ct = default)
            => throw new HttpRequestException("servidor fora");
    }
}
