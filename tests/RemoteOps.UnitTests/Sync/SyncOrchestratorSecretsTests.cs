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
    /// Falha no canal de segredos não derruba o app nem o laço: vira <see cref="SyncState.Error"/> e
    /// o próximo intervalo tenta de novo (offline-first, ADR-002).
    /// </summary>
    [Fact]
    public async Task FalhaNoCanalDeSegredos_ViraErrorSemRelancar()
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

        Assert.Equal(SyncState.Error, orch.Status.State);
    }

    private sealed class RecordingApplier(List<string> order) : IRemoteChangeApplier
    {
        public Task ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default)
        {
            order.Add("metadados");
            return Task.CompletedTask;
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
