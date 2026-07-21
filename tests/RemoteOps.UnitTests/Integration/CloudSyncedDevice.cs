using System.IO;
using System.Net.Http;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Cloud;
using RemoteOps.UnitTests.Sync;

namespace RemoteOps.UnitTests.Integration;

/// <summary>
/// Um PC do operador inteiro, ligado ao SERVIDOR DE VERDADE: banco SQLCipher +
/// <see cref="SqlCipherLocalStore"/> (o mesmo que a UI lê) + cofre enraizado na AMK + os dois canais
/// de sync falando HTTP com o <c>RemoteOps.Cloud</c> hospedado num <c>TestServer</c>.
///
/// <para><b>O que muda em relação ao <c>SyncedDevice</c> dos testes de unidade:</b> lá o único fake
/// era a rede — mas era um fake GRANDE. <c>FakeChangelogApi</c>/<c>FakeSecretsApi</c> substituem
/// justamente a camada que divergiu em produção: rota, serialização do corpo, Bearer + header
/// <c>X-Device-Id</c>, RBAC por membership e os códigos de status. Aqui essa camada é a real: os
/// clientes são <see cref="CloudSyncApiClient"/> e <see cref="SecretsApiClient"/> de produção,
/// apontados no handler do <c>TestServer</c>, e do outro lado está o <c>Program.cs</c> completo.</para>
///
/// <para>Só duas coisas continuam substituídas, e nenhuma delas está no caminho do defeito: o banco
/// do servidor (InMemory em vez de Postgres — ver <see cref="CloudApiFactory"/>) e o cofre da CHAVE
/// DO BANCO local (DPAPI, que não é o objeto do teste). O cofre dos SEGREDOS é o real, e a AMK é a
/// mesma nos dois devices — é o que permite a asserção forte: o B ABRE o que o A selou.</para>
/// </summary>
internal sealed class CloudSyncedDevice : IAsyncDisposable
{
    /// <summary>Workspace do COFRE e dos metadados locais — espelha <c>AppRuntime.CredentialsWorkspace</c>.</summary>
    public const string VaultWorkspaceId = "ws-local";

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private readonly string _dir;
    private readonly AmkWorkspaceKeyRing _keyRing;
    private readonly HttpClient _http;

    private CloudSyncedDevice(
        string dir,
        AmkWorkspaceKeyRing keyRing,
        HttpClient http,
        FileVaultStore envelopeStore,
        CredentialVault vault,
        WorkspaceContext workspace,
        SqlCipherLocalStore store,
        SyncOrchestrator sync,
        ISecretsApi secretsApi,
        string serverWorkspaceId)
    {
        _dir = dir;
        _keyRing = keyRing;
        _http = http;
        EnvelopeStore = envelopeStore;
        Vault = vault;
        Workspace = workspace;
        Store = store;
        Sync = sync;
        SecretsApi = secretsApi;
        ServerWorkspaceId = serverWorkspaceId;
    }

    public FileVaultStore EnvelopeStore { get; }

    public CredentialVault Vault { get; }

    public WorkspaceContext Workspace { get; }

    /// <summary>O store que a UI usa. Se o host não estiver AQUI, ele não existe pro operador.</summary>
    public SqlCipherLocalStore Store { get; }

    public SyncOrchestrator Sync { get; }

    /// <summary>
    /// O cliente REAL do canal de segredos, exposto para o cenário do envelope venenoso: é por ele
    /// que o teste planta no servidor um envelope que este app nunca produziria (como faria um
    /// cliente de outra versão), sem inventar um atalho por dentro do banco.
    /// </summary>
    public ISecretsApi SecretsApi { get; }

    public string ServerWorkspaceId { get; }

    public static async Task<CloudSyncedDevice> CreateAsync(
        string name,
        CloudApiFactory factory,
        byte[] amk,
        string serverWorkspaceId,
        TokenSet tokens,
        Guid deviceId)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-cloudsync-{name}-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        // Cofre das credenciais: raiz AMK (é o que torna o segredo portável entre devices).
        var envelopeStore = new FileVaultStore(Path.Combine(dir, "vault.json"));
        var keyRing = new AmkWorkspaceKeyRing(amk);
        var vault = new CredentialVault(envelopeStore, keyRing, NullVaultAuditSink.Instance);

        // Banco local: a chave dele mora noutro cofre (fake) de propósito — o objeto do teste é o
        // sync, não o DPAPI da chave do banco.
        var localFactory = new LocalSyncClientFactory(new FakeCredentialVault(), dir);
        WorkspaceContext workspace = await localFactory.OpenWorkspaceAsync(VaultWorkspaceId);

        var store = new SqlCipherLocalStore(workspace);
        var metadata = new SqliteSyncMetadataStore(workspace);
        var applier = new LocalEntitiesChangeApplier(workspace);

        // ── A camada que nenhum teste exercitava ──────────────────────────────────────────
        // HttpClient do TestServer + os DOIS clientes de produção. Cada device tem o seu deviceId,
        // porque o servidor exige X-Device-Id em toda request de sync e o RBAC recusa device revogado.
        HttpClient http = factory.CreateClient();
        var tokenStore = new FakeTokenStore(tokens);
        var changelogApi = new CloudSyncApiClient(http, deviceId, tokenStore);
        var secretsApi = new SecretsApiClient(http, deviceId, tokenStore);

        var secretSync = new SecretSyncOrchestrator(
            serverWorkspaceId, VaultWorkspaceId, envelopeStore, secretsApi, metadata);
        var sync = new SyncOrchestrator(
            serverWorkspaceId, workspace.SyncClient, changelogApi, applier, metadata,
            pageSize: 200, secrets: secretSync);

        return new CloudSyncedDevice(
            dir, keyRing, http, envelopeStore, vault, workspace, store, sync, secretsApi, serverWorkspaceId);
    }

    /// <summary>
    /// Roda um ciclo e EXIGE que os metadados tenham passado. O <see cref="SyncOrchestrator"/> engole
    /// exceção de propósito (offline-first: marca Error e tenta depois), então um teste que só
    /// chamasse <c>SyncOnceAsync</c> passaria com o sync inteiro quebrado — inclusive com 401 ou 404,
    /// que é exatamente o que esta suíte existe para pegar.
    ///
    /// <para>A saúde do canal de SEGREDOS não é checada aqui: ela é asserção dos testes (um ciclo
    /// <c>Degraded</c> é resultado legítimo quando existe envelope venenoso no servidor).</para>
    /// </summary>
    public async Task SyncOnceAsync()
    {
        await Sync.SyncOnceAsync();
        if (Sync.Status.State == SyncState.Error)
        {
            throw new InvalidOperationException(
                "O ciclo de sync terminou em Error — o orquestrador engoliu a exceção (offline-first). " +
                "Chame o cliente HTTP direto para ver o status que o servidor devolveu.");
        }
    }

    /// <summary>Sela um segredo no cofre, como o app faz numa senha de chaveiro ou inline de device.</summary>
    public Task<SecretEnvelope> SealAsync(string credentialId, string secret, string type = "password") =>
        Vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = VaultWorkspaceId,
                CredentialId = credentialId,
                Type = type,
                ActorUserId = "op",
            },
            secret.AsMemory());

    /// <summary>Abre um segredo do cofre. Lança se a raiz/AAD não baterem — é a asserção forte.</summary>
    public Task<VaultSecret> OpenAsync(string envelopeId) =>
        Vault.RetrieveAsync(envelopeId, new VaultAccessContext { ActorUserId = "op" });

    public Task<IReadOnlyList<SecretEnvelope>> ListEnvelopesAsync() =>
        EnvelopeStore.ListEnvelopesAsync(VaultWorkspaceId);

    /// <summary>
    /// Apaga um campo do patch JÁ GRAVADO na fila de envio — a reprodução fiel do defeito de produção.
    ///
    /// <para><b>Por que isto não é trapaça:</b> o outbox CONGELA o patch no momento da edição (o
    /// <c>LocalSyncClient</c> serializa o patch em <c>patch_json</c> e o envio relê esse blob, sem
    /// reconstruir do registro atual). Os ~700 devices foram cadastrados numa versão em que o patch do
    /// endpoint saía sem <c>credential_ref_id</c>, e essa linha ficou incompleta PARA SEMPRE. Reescrever
    /// a linha da fila é a única forma honesta de recriar esse estado com o código de hoje: o banco
    /// local continua completo (o operador VÊ o vínculo no PC A), e só o que sobe está furado — que é
    /// precisamente o quadro relatado.</para>
    /// </summary>
    /// <returns>Quantas linhas da fila foram alteradas.</returns>
    public async Task<int> DropFieldFromQueuedPatchAsync(
        string entityType, string entityId, string field, CancellationToken ct = default)
    {
        using SqliteConnection conn = await Workspace.OpenConnectionAsync(ct);

        var rows = new List<(long Id, string PatchJson)>();
        using (SqliteCommand select = conn.CreateCommand())
        {
            select.CommandText =
                "SELECT id, patch_json FROM local_outbox WHERE entity_type = $et AND entity_id = $eid";
            select.Parameters.AddWithValue("$et", entityType);
            select.Parameters.AddWithValue("$eid", entityId);

            using SqliteDataReader reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        int changed = 0;
        foreach ((long id, string patchJson) in rows)
        {
            Dictionary<string, JsonElement> patch =
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(patchJson, s_json) ?? [];
            if (!patch.Remove(field))
            {
                continue;
            }

            using SqliteCommand update = conn.CreateCommand();
            update.CommandText = "UPDATE local_outbox SET patch_json = $patch WHERE id = $id";
            update.Parameters.AddWithValue("$patch", JsonSerializer.Serialize(patch, s_json));
            update.Parameters.AddWithValue("$id", id);
            changed += await update.ExecuteNonQueryAsync(ct);
        }

        return changed;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _http.Dispose();
        _keyRing.Dispose();
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // arquivo do SQLite ainda preso — não é o objeto do teste.
        }
    }
}
