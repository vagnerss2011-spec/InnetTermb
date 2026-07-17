using System.IO;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// <see cref="IAccountVaultActivator"/> de produção: migra o cofre local da raiz DPAPI pra AMK e
/// devolve o cofre AMK-rooted que o resto do app usa (spec §4.1/§7).
///
/// <para><b>Ordem:</b> migra ANTES de publicar o <see cref="Vault"/>. O <c>LocalVaultMigrator</c>
/// constrói a raiz nova por dentro (justamente pra selar com o mesmo rooting que o app vai usar pra
/// abrir), então quando o cofre novo aparece aqui ele já está consistente. Publicar o cofre antes
/// deixaria uma janela em que o app enxerga a raiz nova e os envelopes ainda estão na velha.</para>
/// </summary>
public sealed class VaultRootActivator : IAccountVaultActivator, IDisposable
{
    private readonly FileVaultStore _fileStore;
    private readonly IWorkspaceKeyRing _legacyKeyRing;
    private readonly string _tokenRefPath;

    private AmkWorkspaceKeyRing? _amkKeyRing;

    /// <param name="fileStore">O cofre em disco — é <see cref="IVaultMigrationStore"/> e key store.</param>
    /// <param name="legacyKeyRing">Raiz DPAPI atual: só serve pra ABRIR o que já está selado.</param>
    /// <param name="tokenRefPath">Arquivo que guarda só o envelopeId dos tokens (não o token).</param>
    public VaultRootActivator(
        FileVaultStore fileStore,
        IWorkspaceKeyRing legacyKeyRing,
        string tokenRefPath)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(legacyKeyRing);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenRefPath);

        _fileStore = fileStore;
        _legacyKeyRing = legacyKeyRing;
        _tokenRefPath = tokenRefPath;
    }

    /// <summary>
    /// O cofre AMK-rooted, pro <c>AppCompositionRoot</c>. <c>null</c> antes do
    /// <see cref="ActivateAsync"/> — o App cai no cofre DPAPI (modo local) nesse caso.
    /// </summary>
    public CredentialVault? Vault { get; private set; }

    public async Task<ITokenStore> ActivateAsync(
        ReadOnlyMemory<byte> amk,
        string syncWorkspaceId,
        IReadOnlyList<string> vaultWorkspaceIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vaultWorkspaceIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncWorkspaceId);

        // Migra TODOS os workspaces locais antes de trocar a raiz do app. Idempotente (no-op se já
        // migrado), então roda em todo login/startup sem custo depois da primeira vez.
        var migrator = new LocalVaultMigrator(_fileStore, _legacyKeyRing);
        foreach (string workspaceId in vaultWorkspaceIds)
        {
            await migrator.MigrateWorkspaceAsync(workspaceId, amk, ct).ConfigureAwait(false);
        }

        _amkKeyRing = new AmkWorkspaceKeyRing(amk.Span);
        Vault = new CredentialVault(_fileStore, _amkKeyRing, new InMemoryVaultAuditSink());

        return new VaultTokenStore(Vault, syncWorkspaceId, _tokenRefPath);
    }

    public void Dispose() => _amkKeyRing?.Dispose();
}

/// <summary>
/// <see cref="IAccountSyncStarter"/> de produção: monta a <see cref="SyncSession"/> pelo
/// <see cref="SyncSessionFactory"/>. Delegado porque a sessão precisa do <c>WorkspaceContext</c>
/// (SQLCipher), que só existe depois do cofre — e o App é quem sabe montá-lo.
/// </summary>
public sealed class DelegateSyncStarter : IAccountSyncStarter
{
    private readonly Func<string, CancellationToken, Task> _start;

    public DelegateSyncStarter(Func<string, CancellationToken, Task> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
    }

    public Task StartAsync(string workspaceId, CancellationToken ct = default) => _start(workspaceId, ct);
}
