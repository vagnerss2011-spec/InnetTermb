using System.Windows;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Escolher o cofre ao abrir (Fatia 1). Abre com <see cref="Window.ShowDialog"/> e quem chamou lê o
/// <c>WorkspaceChoiceViewModel.Chosen</c> depois: preenchido = escolheu; <c>null</c> = fechou no X,
/// e isso é DESISTÊNCIA — nunca "abre o primeiro".
///
/// <para>Quem sinaliza a escolha é o VM, e não o <c>DialogResult</c> desta janela, de propósito:
/// atribuir <c>DialogResult</c> numa janela aberta com <c>Show()</c> (é assim que o teste de render
/// a abre) estoura <c>InvalidOperationException</c>. Um estado que só funciona no modo modal é
/// armadilha para quem reusar a janela depois.</para>
/// </summary>
public partial class WorkspaceChoiceWindow : Window
{
    public WorkspaceChoiceWindow(WorkspaceChoiceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Confirmed += (_, _) => Close();
    }
}
