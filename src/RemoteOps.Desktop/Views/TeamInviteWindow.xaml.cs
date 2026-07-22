using System.Windows;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Convite do time (Fatia 1): gerar (dono) e aceitar (convidado) na mesma janela, em modos
/// diferentes.
///
/// <para>A janela NÃO se fecha sozinha no aceite: o recado que fica na tela — inclusive o "feche e
/// abra o RemoteOps", quando a sessão precisa ser renovada — é o que evita o colega achar que o
/// cofre do time está quebrado. Quem abriu lê <see cref="Joined"/> depois que ela fecha.</para>
///
/// <para><see cref="Joined"/> em vez de <c>DialogResult</c> de propósito: atribuir
/// <c>DialogResult</c> numa janela aberta com <c>Show()</c> (é assim que o teste de render a abre)
/// estoura <c>InvalidOperationException</c> — e um estado que só existe no modo modal seria uma
/// armadilha para quem reusar esta janela depois.</para>
/// </summary>
public partial class TeamInviteWindow : Window
{
    public TeamInviteWindow(TeamInviteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Accepted += (_, _) => Joined = true;
        viewModel.TeamCreated += (_, _) => CreatedTeam = true;
    }

    /// <summary>O convidado entrou num time nesta sessão da janela.</summary>
    public bool Joined { get; private set; }

    /// <summary>
    /// Um time foi fundado nesta sessão da janela. Quem abriu usa isto para reavaliar o indicador de
    /// cofre: a chave do time acabou de nascer neste computador.
    /// </summary>
    public bool CreatedTeam { get; private set; }

    private TeamInviteViewModel Vm => (TeamInviteViewModel)DataContext;

    private async void Generate_Click(object sender, RoutedEventArgs e) => await Vm.GenerateAsync();

    private async void Accept_Click(object sender, RoutedEventArgs e) => await Vm.AcceptAsync();

    private async void CreateTeam_Click(object sender, RoutedEventArgs e) => await Vm.CreateTeamAsync();
}
