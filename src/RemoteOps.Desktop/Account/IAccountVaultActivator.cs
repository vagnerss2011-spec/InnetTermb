using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// O que o App sabe montar a partir da AMK — e que o <see cref="AccountSyncCoordinator"/> não pode
/// conhecer sem virar untestável: FileVaultStore, CredentialVault, LocalVaultMigrator, DPAPI.
///
/// <para>"Ativar" é uma operação só de propósito, e não dois métodos (trocar raiz + migrar): as duas
/// coisas têm que acontecer juntas ou nenhuma. Um cofre cuja raiz virou AMK mas cujos envelopes
/// continuam selados sob DPAPI é um cofre que abre a UI e não decifra senha nenhuma — pior que uma
/// falha explícita.</para>
/// </summary>
public interface IAccountVaultActivator
{
    /// <summary>
    /// Troca a raiz do cofre local pra <c>AmkWorkspaceKeyRing</c> (spec §4.1) e re-sela o que ainda
    /// estiver sob a raiz DPAPI antiga (spec §7 — no-op se já migrado). Devolve o
    /// <see cref="ITokenStore"/> DO COFRE JÁ ATIVADO: é ele quem guarda os tokens, e ele só pode
    /// existir depois da troca de raiz.
    /// </summary>
    /// <param name="amk">A AMK (32B). O ativador não toma posse — o chamador ainda a zera.</param>
    /// <param name="syncWorkspaceId">
    /// Workspace do SERVIDOR (GUID): é sob ele que o <c>VaultTokenStore</c> guarda os tokens. Vem
    /// como parâmetro (e não no construtor) porque só é conhecido DEPOIS do login/cache — o ativador
    /// precisa existir antes disso.
    /// </param>
    /// <param name="vaultWorkspaceIds">
    /// Workspaces LOCAIS do cofre a migrar (identidade local, não a do servidor). São VÁRIOS: além
    /// das credenciais do operador ("ws-local"), a chave do banco SQLCipher é ela própria um segredo
    /// do cofre, guardada sob "local" (ver <c>VaultDbKeyProvider</c>). Deixar um de fora não degrada
    /// — trava o app.
    /// </param>
    Task<ITokenStore> ActivateAsync(
        ReadOnlyMemory<byte> amk,
        string syncWorkspaceId,
        IReadOnlyList<string> vaultWorkspaceIds,
        CancellationToken ct = default);
}

/// <summary>
/// Liga o <c>SyncSession</c> (SyncSessionFactory + tokens do cofre). Separado do ativador porque
/// tem uma postura DIFERENTE em relação a falhas: o cofre é obrigatório, o sync é best-effort.
/// </summary>
public interface IAccountSyncStarter
{
    /// <param name="workspaceId">Workspace no SERVIDOR (GUID) — distinto da identidade local.</param>
    Task StartAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>Conta ativa neste device depois de um login ou de um relaunch pelo cache.</summary>
/// <param name="Email">Identidade da conta.</param>
/// <param name="WorkspaceId">Workspace no servidor (GUID).</param>
public sealed record AccountActivation(string Email, string WorkspaceId);
