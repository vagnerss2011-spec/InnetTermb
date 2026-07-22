using System.Windows;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Tela de Equipe (Fatia 1e): quem está no time, convidar e remover.
///
/// <para>A janela carrega a lista sozinha no <c>Loaded</c> — se o operador tivesse de clicar em
/// "Atualizar" para ver alguém, a primeira coisa que ele veria seria uma janela vazia, exatamente a
/// impressão que esta tela existe para evitar. O <c>await</c> é solto de propósito: o
/// <see cref="TeamViewModel.LoadAsync"/> não relança (falha vira texto na tela), então não há erro
/// para engolir aqui.</para>
/// </summary>
public partial class TeamWindow : Window
{
    public TeamWindow(TeamViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.InviteRequested += (_, _) => InviteRequested?.Invoke(this, System.EventArgs.Empty);
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }

    /// <summary>
    /// Pedido de abrir a janela de convite. Repassado para fora em vez de aberto aqui porque quem
    /// sabe montar o <c>TeamInviteViewModel</c> (serviço + workspace) é quem abriu esta janela.
    /// </summary>
    public event System.EventHandler? InviteRequested;
}
