using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// Quem decide em QUAL cofre o app vai abrir quando a conta enxerga mais de um workspace (pessoal e
/// time). É uma interface, e não a janela direto, porque a escolha acontece dentro do fluxo de login
/// — que precisa ser testável sem WPF.
///
/// <para><b>Só é consultado com 2 ou mais workspaces.</b> Com um só, perguntar seria atrito puro:
/// não há escolha a fazer, e uma tela a mais no boot diário é exatamente o tipo de coisa que faz o
/// operador odiar uma feature que ele nem pediu.</para>
/// </summary>
public interface IWorkspaceChooser
{
    /// <summary>
    /// Devolve o workspace escolhido, ou <c>null</c> se o operador desistiu (fechou a janela).
    /// Desistir NÃO pode virar "abre o primeiro": abrir o cofre errado é como se cadastra host de
    /// cliente no cofre pessoal por engano.
    /// </summary>
    Task<AccountWorkspace?> ChooseAsync(
        IReadOnlyList<AccountWorkspace> workspaces, CancellationToken ct = default);
}

/// <summary>
/// O operador fechou a tela de escolha do cofre. Não é falha de rede nem senha errada — é
/// desistência, e a UI trata como "volta pro login sem berrar".
/// </summary>
public sealed class WorkspaceChoiceCancelledException : Exception
{
    public WorkspaceChoiceCancelledException()
        : base("A escolha do cofre foi cancelada.")
    {
    }
}
