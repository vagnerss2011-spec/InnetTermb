using System.IO;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Um PC do operador inteiro, em miniatura: banco SQLCipher + <see cref="SqlCipherLocalStore"/> (o
/// MESMO que a UI lê) + cofre real enraizado na AMK + os dois canais de sync (changelog e segredos)
/// apontando pros mesmos servidores fake.
///
/// <para>Estende o <see cref="SecretSyncDevice"/> em ambição: lá o alvo era só a cripto do envelope,
/// aqui o alvo é o ESTADO VISÍVEL — o que aparece na lista de hosts. Por isso nada de fake no
/// caminho de dados: o store é o de produção, o applier é o de produção, o orquestrador é o de
/// produção. O único fake é a REDE (e o cofre da chave do BANCO, que é DPAPI e não é o objeto do
/// teste).</para>
/// </summary>
internal sealed class SyncedDevice : IAsyncDisposable
{
    /// <summary>Workspace do COFRE e dos metadados locais — espelha <c>AppRuntime.CredentialsWorkspace</c>.</summary>
    public const string VaultWorkspaceId = "ws-local";

    private readonly string _dir;
    private readonly AmkWorkspaceKeyRing _keyRing;

    private SyncedDevice(
        string dir, AmkWorkspaceKeyRing keyRing, FileVaultStore envelopeStore, CredentialVault vault,
        WorkspaceContext workspace, SqlCipherLocalStore store, SyncOrchestrator sync)
    {
        _dir = dir;
        _keyRing = keyRing;
        EnvelopeStore = envelopeStore;
        Vault = vault;
        Workspace = workspace;
        Store = store;
        Sync = sync;
    }

    public FileVaultStore EnvelopeStore { get; }

    public CredentialVault Vault { get; }

    public WorkspaceContext Workspace { get; }

    /// <summary>O store que a UI usa. Se o host não estiver AQUI, ele não existe pro operador.</summary>
    public SqlCipherLocalStore Store { get; }

    public SyncOrchestrator Sync { get; }

    public static async Task<SyncedDevice> CreateAsync(
        string name,
        byte[] amk,
        FakeChangelogApi changelog,
        FakeSecretsApi secrets,
        string serverWorkspaceId)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-device-{name}-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);

        // Cofre das credenciais: raiz AMK (é o que torna o segredo portável entre devices).
        var envelopeStore = new FileVaultStore(Path.Combine(dir, "vault.json"));
        var keyRing = new AmkWorkspaceKeyRing(amk);
        var vault = new CredentialVault(envelopeStore, keyRing, NullVaultAuditSink.Instance);

        // Banco local: a chave dele mora noutro cofre (fake) de propósito — o objeto do teste é o
        // sync dos metadados, não o DPAPI da chave do banco.
        var factory = new LocalSyncClientFactory(new FakeCredentialVault(), dir);
        WorkspaceContext workspace = await factory.OpenWorkspaceAsync(VaultWorkspaceId);

        var store = new SqlCipherLocalStore(workspace);
        var metadata = new SqliteSyncMetadataStore(workspace);
        var applier = new LocalEntitiesChangeApplier(workspace);
        var secretSync = new SecretSyncOrchestrator(
            serverWorkspaceId, VaultWorkspaceId, envelopeStore, secrets, metadata);
        var sync = new SyncOrchestrator(
            serverWorkspaceId, workspace.SyncClient, changelog, applier, metadata,
            pageSize: 200, secrets: secretSync);

        return new SyncedDevice(dir, keyRing, envelopeStore, vault, workspace, store, sync);
    }

    /// <summary>
    /// Roda um ciclo e EXIGE sucesso. O <see cref="SyncOrchestrator"/> engole exceção de propósito
    /// (offline-first: marca Error e tenta depois), então um teste que só chamasse SyncOnceAsync
    /// passaria com o sync quebrado. Aqui a falha aparece.
    /// </summary>
    public async Task SyncOnceAsync()
    {
        await Sync.SyncOnceAsync();
        if (Sync.Status.State == SyncState.Error)
        {
            throw new InvalidOperationException(
                $"O ciclo de sync terminou em Error — o orquestrador engoliu a exceção (offline-first). " +
                $"Rode o applier/API direto pra ver a causa.");
        }
    }

    /// <summary>Sela um segredo no cofre, como o app faz numa senha inline de device.</summary>
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

    /// <summary>Abre um segredo do cofre. Lança se a raiz/AAD não baterem.</summary>
    public Task<VaultSecret> OpenAsync(string envelopeId) =>
        Vault.RetrieveAsync(envelopeId, new VaultAccessContext { ActorUserId = "op" });

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
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
