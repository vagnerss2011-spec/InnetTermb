using System.Security.Cryptography;

using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// Resultado de um login/registro bem-sucedido: o que o app precisa pra ligar o sync E2EE.
/// Classe (não record) de propósito — <see cref="Amk"/> é material de chave vivo, e a igualdade
/// estrutural/ToString() de um record convidariam a comparar ou logar a AMK sem querer.
///
/// Ciclo de vida: quem consome chama <see cref="ZeroAmk"/> quando termina. A UI zera a sessão que
/// ninguém consumiu (janela fechada no meio) — ver <c>AccountViewModel.ClearSession</c>.
/// </summary>
public sealed class AccountSession
{
    public AccountSession(
        string email,
        string workspaceId,
        byte[] amk,
        TokenSet tokens,
        IReadOnlyList<AccountWorkspace> workspaces,
        string? recoveryKey = null)
    {
        Email = email;
        WorkspaceId = workspaceId;
        Amk = amk;
        Tokens = tokens;
        Workspaces = workspaces;
        RecoveryKey = recoveryKey;
    }

    public string Email { get; }

    /// <summary>
    /// Workspace ATIVO desta sessão no servidor (GUID). Fase 1 é mono-workspace (spec §11); é este
    /// id que o sync usa e que entra na entropia do cache da AMK.
    /// </summary>
    public string WorkspaceId { get; }

    /// <summary>Raiz portável do cofre (32B). Nunca serializar, logar ou mandar pra rede.</summary>
    public byte[] Amk { get; }

    /// <summary>Tokens do backend — persistidos no VaultTokenStore pelo <c>AccountSyncCoordinator</c>.</summary>
    public TokenSet Tokens { get; }

    public IReadOnlyList<AccountWorkspace> Workspaces { get; }

    /// <summary>
    /// Chave de recuperação — preenchida SÓ no registro, pra ser exibida uma única vez. É string
    /// porque a UI a exibe e a copia (e <c>RecoveryKeyCodec.Generate</c> já devolve string): não dá
    /// pra zerar da memória como um char[]. Aceito: ela é exibida ao dono da conta na própria
    /// máquina dele, vive um diálogo, e some com o GC. O escrow no servidor continua cifrado.
    /// </summary>
    public string? RecoveryKey { get; }

    /// <summary>Zera a AMK. Idempotente.</summary>
    public void ZeroAmk() => CryptographicOperations.ZeroMemory(Amk);
}
