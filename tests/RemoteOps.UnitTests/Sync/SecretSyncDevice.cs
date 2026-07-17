using System.IO;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Um device de verdade, em miniatura: cofre em arquivo (<see cref="FileVaultStore"/>) + raiz AMK
/// (<see cref="AmkWorkspaceKeyRing"/>) + o transporte de segredos apontando pro mesmo servidor fake.
///
/// <para>Usa o cofre REAL (não um fake de vault) porque a prova da fase é criptográfica: o device B
/// tem que ABRIR o que o device A selou. Um fake de cofre provaria só que dois dicionários batem.</para>
///
/// <para>O <see cref="VaultWorkspaceId"/> ("ws-local") é a identidade sob a qual os segredos são
/// selados — é ela que entra na derivação da WDK e no AAD. O <see cref="ServerWorkspaceId"/> é o GUID
/// do workspace no servidor. São coisas diferentes de propósito (ver <c>AppRuntime</c>), e o
/// transporte é quem mapeia uma na outra.</para>
/// </summary>
internal sealed class SecretSyncDevice : IDisposable
{
    /// <summary>Workspace do COFRE — espelha <c>AppRuntime.CredentialsWorkspace</c>.</summary>
    public const string VaultWorkspaceId = "ws-local";

    private readonly string _dir;
    private readonly AmkWorkspaceKeyRing _keyRing;

    public SecretSyncDevice(
        string name, ReadOnlySpan<byte> amk, FakeSecretsApi api, string serverWorkspaceId, int pageSize = 200)
    {
        _dir = Path.Combine(Path.GetTempPath(), $"remoteops-secretsync-{name}-{Guid.NewGuid():n}");
        Store = new FileVaultStore(Path.Combine(_dir, "vault.json"));
        _keyRing = new AmkWorkspaceKeyRing(amk);
        Vault = new CredentialVault(Store, _keyRing, NullVaultAuditSink.Instance);
        Metadata = new FakeSyncMetadataStore();
        Secrets = new SecretSyncOrchestrator(
            serverWorkspaceId, VaultWorkspaceId, Store, api, Metadata, pageSize: pageSize);
    }

    public FileVaultStore Store { get; }

    public CredentialVault Vault { get; }

    public FakeSyncMetadataStore Metadata { get; }

    public SecretSyncOrchestrator Secrets { get; }

    /// <summary>Sela um segredo no cofre deste device, como o app faz numa senha inline de device.</summary>
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

    /// <summary>Abre um segredo do cofre deste device. Lança se a raiz/AAD não baterem.</summary>
    public Task<VaultSecret> OpenAsync(string envelopeId) =>
        Vault.RetrieveAsync(envelopeId, new VaultAccessContext { ActorUserId = "op" });

    public Task<IReadOnlyList<SecretEnvelope>> ListAsync() => Store.ListEnvelopesAsync(VaultWorkspaceId);

    public void Dispose()
    {
        _keyRing.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
