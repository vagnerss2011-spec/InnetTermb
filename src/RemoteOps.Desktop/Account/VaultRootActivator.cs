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
    private WkWorkspaceKeyRing? _teamKeyRing;

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

    /// <summary>
    /// A raiz da chave do TIME (WK aleatória compartilhada), viva enquanto a conta estiver ativa.
    /// É por ela que o convite entra e sai: o dono sorteia a WK ao convidar, o convidado a IMPORTA
    /// ao aceitar. <c>null</c> antes do <see cref="ActivateAsync"/> — sem AMK não há como
    /// embrulhá-la em disco.
    ///
    /// <para>É a MESMA instância que o <see cref="Vault"/> usa para os workspaces <c>time:</c> — e
    /// não uma segunda visão do mesmo cofre. O que separa os dois papéis não é o objeto, é a
    /// INTERFACE: o cofre enxerga só o <c>IWorkspaceKeyRing</c> (que não sabe criar chave), e o
    /// fluxo de convite chama o tipo concreto (que sabe). Duas instâncias com bandeiras diferentes
    /// sobre o mesmo store seriam duas fontes da verdade — o defeito estrutural desta base.</para>
    /// </summary>
    public WkWorkspaceKeyRing? TeamKeyRing => _teamKeyRing;

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
        // ⚠️ Boot: a lista efetiva inclui o cofre do TIME deste workspace. Um workspace de fora da
        // lista não degrada — trava o app na abertura (já mordeu duas vezes nesta base). Decidido
        // AQUI, num lugar só, em cima da lista que este ativador recebeu.
        var migrator = new LocalVaultMigrator(_fileStore, _legacyKeyRing);
        foreach (string workspaceId in AppRuntime.VaultWorkspacesFor(syncWorkspaceId, vaultWorkspaceIds))
        {
            // ⚠️ Cofre de TIME não migra. A migração DPAPI→AMK carimbaria o workspace como
            // "derivado da conta", e a chave dele é o oposto disso: aleatória e COMPARTILHADA. O
            // carimbo errado não daria erro nenhum agora — só faria o cofre do time abrir com a
            // chave errada depois, que é a falha silenciosa que esta fatia inteira tenta evitar.
            // Ele também nunca teve fase DPAPI: nasce sob a WK, no aceite do convite.
            if (AppRuntime.IsTeamVaultWorkspace(workspaceId))
            {
                continue;
            }

            await migrator.MigrateWorkspaceAsync(workspaceId, amk, ct).ConfigureAwait(false);
        }

        _amkKeyRing = new AmkWorkspaceKeyRing(amk.Span);

        // UMA instância, servindo o COFRE e o fluxo de CONVITE ao mesmo tempo. Antes eram duas (uma
        // com criação permitida, outra negada) sobre o MESMO store — duas visões do mesmo cofre é a
        // classe de defeito nº 1 desta base. Agora o fail-closed é do TIPO: MintWorkspaceKeyAsync
        // (o ato de fundar o time) não está no IWorkspaceKeyRing, então o cofre não o alcança, e o
        // fluxo de convite chama o tipo concreto.
        _teamKeyRing = new WkWorkspaceKeyRing(_fileStore, _fileStore, amk.Span);

        // O cofre atende as DUAS raízes. Consequência que é o núcleo do desenho: `ws-local` (as
        // credenciais), `local` (a chave do banco SQLCipher) e o GUID dos tokens não começam com
        // "time:", então continuam na raiz da AMK — nenhum segredo que é da MÁQUINA passa a ser
        // procurado no cofre compartilhado. Era essa a objeção do 1d, e ela se dissolve aqui.
        Vault = new CredentialVault(
            _fileStore,
            new RoutedWorkspaceKeyRing(_amkKeyRing, [(AppRuntime.TeamVaultPrefix, _teamKeyRing)]),
            new InMemoryVaultAuditSink());

        return new VaultTokenStore(Vault, syncWorkspaceId, _tokenRefPath);
    }

    public void Dispose()
    {
        _amkKeyRing?.Dispose();
        _teamKeyRing?.Dispose();
    }
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
