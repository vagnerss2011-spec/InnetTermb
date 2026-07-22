using System;
using System.Collections.Generic;
using System.Linq;

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

    public string Explanation =>
        "Sua conta tem acesso a mais de um cofre. Tudo o que você cadastrar (clientes, equipamentos "
        + "e senhas) fica no cofre escolhido aqui. Para trocar de cofre, saia da conta e entre de novo.";

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
