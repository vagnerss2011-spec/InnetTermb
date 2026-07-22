using System;
using System.Collections.Generic;
using System.Linq;

using RemoteOps.Desktop.Account;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Um cofre na lista de escolha. Mostra o NOME e o PAPEL, que é tudo o que o servidor sabe dizer no
/// login — inventar um rótulo "pessoal/time" aqui seria adivinhação, e adivinhar qual cofre é qual é
/// justamente o erro que esta tela existe para impedir.
/// </summary>
public sealed class WorkspaceChoiceItem
{
    public WorkspaceChoiceItem(AccountWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Workspace = workspace;
    }

    public AccountWorkspace Workspace { get; }

    public string Name => string.IsNullOrWhiteSpace(Workspace.Name) ? Workspace.Id : Workspace.Name;

    /// <summary>Legenda do item. O papel importa: ele diz o que a pessoa pode fazer ali dentro.</summary>
    public string RoleLabel => $"Seu papel: {Workspace.Role}";
}

/// <summary>
/// "Em qual cofre você quer entrar?" — a tela que aparece quando a conta enxerga MAIS DE UM
/// workspace (o pessoal e o do time). Com um só ela nunca aparece: o
/// <c>E2eeAccountAuthenticator</c> nem consulta o chooser, porque não há escolha a fazer e uma tela
/// a mais no boot diário é atrito puro.
///
/// <para>O que está escrito na tela é parte da feature, não enfeite: o operador precisa saber que o
/// que ele cadastrar depois vai para o cofre que escolher AGORA — cadastrar o host do cliente no
/// cofre pessoal por engano é o incidente que esta fatia tenta evitar.</para>
/// </summary>
public sealed class WorkspaceChoiceViewModel : BaseViewModel
{
    private WorkspaceChoiceItem? _selected;

    public WorkspaceChoiceViewModel(IReadOnlyList<AccountWorkspace> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        if (workspaces.Count == 0)
        {
            throw new ArgumentException("Não há workspace para escolher.", nameof(workspaces));
        }

        Workspaces = [.. workspaces.Select(w => new WorkspaceChoiceItem(w))];

        // Pré-seleciona o primeiro só para o botão nascer habilitado; a ESCOLHA continua sendo um
        // ato explícito (o operador confirma), e é isso que diferencia esta tela do workspaces[0].
        _selected = Workspaces[0];

        ConfirmCommand = new RelayCommand(Confirm, () => Selected is not null);
    }

    /// <summary>O operador confirmou. A janela fecha e devolve <see cref="Chosen"/>.</summary>
    public event EventHandler? Confirmed;

    public IReadOnlyList<WorkspaceChoiceItem> Workspaces { get; }

    public string Title => "Em qual cofre você quer entrar?";

    /// <summary>
    /// ⚠️ <b>A instrução tem de nomear um controle QUE EXISTE.</b> Esta tela nasceu no 1d, antes de o
    /// botão de trocar de cofre existir, e mandava <i>"saia da conta e entre de novo"</i> — não há
    /// "Sair da conta" em lugar nenhum do RemoteOps. O operador procuraria, não acharia, e concluiria
    /// o de sempre: que o recurso não funciona. É a mesma classe de defeito das outras quatro
    /// mensagens que diziam "feche e abra o RemoteOps", achada por outro caminho.
    ///
    /// <para>Por isso o "como" sai da MESMA constante das outras (<see cref="VaultSwitchText"/>):
    /// rótulo do botão e instrução não podem divergir em compilação nenhuma, e um teste afirma que
    /// nenhuma das cinco voltou a mandar o operador para um caminho que a tela não tem.</para>
    /// </summary>
    public const string ExplanationText =
        "Sua conta tem acesso a mais de um cofre. Tudo o que você cadastrar (clientes, equipamentos "
        + "e senhas) fica no cofre escolhido aqui. Para trocar de cofre depois, "
        + VaultSwitchText.HowToSwitch;

    /// <summary>O que está escrito na tela — a constante acima, para um teste poder afirmá-la.</summary>
    public string Explanation => ExplanationText;

    public WorkspaceChoiceItem? Selected
    {
        get => _selected;
        set
        {
            Set(ref _selected, value);
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>O workspace escolhido, ou <c>null</c> enquanto ninguém confirmou.</summary>
    public AccountWorkspace? Chosen { get; private set; }

    public RelayCommand ConfirmCommand { get; }

    private void Confirm()
    {
        if (Selected is null)
        {
            return;
        }

        Chosen = Selected.Workspace;
        Confirmed?.Invoke(this, EventArgs.Empty);
    }
}
