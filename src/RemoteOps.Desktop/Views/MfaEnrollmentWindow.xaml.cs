using System.Windows;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Janela de ativar/desativar 2FA (spec Fase 3). Modal, aberta a partir das Configurações quando há
/// conta na nuvem ativa. Fecha quando o VM sinaliza <see cref="MfaEnrollmentViewModel.Completed"/>
/// (concluiu ou o operador clicou em Fechar).
/// </summary>
public partial class MfaEnrollmentWindow : Window
{
    public MfaEnrollmentWindow(MfaEnrollmentViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
    }
}
