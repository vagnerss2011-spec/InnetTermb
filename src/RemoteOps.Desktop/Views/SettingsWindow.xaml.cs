using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel, string? initialTab = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => Close();
        viewModel.MfaSetupRequested += OnMfaSetupRequested;
        viewModel.TeamInviteRequested += OnTeamInviteRequested;
        viewModel.TeamManagementRequested += OnTeamManagementRequested;
        // O restart vive no App (ele segura o mutex de instância única). Fechar a janela primeiro
        // não faz mal: o RestartApplication encerra o processo inteiro logo em seguida.
        viewModel.RestartRequested += (_, _) => (Application.Current as App)?.RestartApplication();
        if (initialTab is not null)
        {
            SelectTabByHeader(initialTab);
        }
    }

    /// <summary>Abre a janela de 2FA (modal). O IMfaApi autenticado vem do VM (null nunca chega aqui:
    /// o comando só dispara quando CanManageMfa é true).</summary>
    private void OnMfaSetupRequested(object? sender, EventArgs e)
    {
        if (Vm.MfaApi is not { } mfaApi)
        {
            return;
        }

        var window = new MfaEnrollmentWindow(new MfaEnrollmentViewModel(mfaApi)) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>
    /// Abre a janela de convite do time (modal), no modo pedido. O contexto (serviço + workspace
    /// ativo) vem do VM — null nunca chega aqui, os comandos só disparam com <c>CanManageTeam</c>.
    /// </summary>
    private void OnTeamInviteRequested(object? sender, TeamInviteMode mode)
    {
        if (Vm.Team is not { } team)
        {
            return;
        }

        ShowInviteWindow(team, mode, this);
    }

    /// <summary>
    /// Abre a tela de Equipe (modal). O indicador de cofre vem do VM do shell (a MESMA instância que
    /// a barra de status usa): duas cópias do estado divergiriam, e a tela de Equipe é justamente
    /// onde a divergência seria mais cara de acreditar.
    /// </summary>
    private void OnTeamManagementRequested(object? sender, EventArgs e)
    {
        if (Vm.Team is not { } team)
        {
            return;
        }

        var teamViewModel = new TeamViewModel(
            team.Api, team.WorkspaceId, team.SessionKind, Vm.VaultBadge);
        var window = new TeamWindow(teamViewModel) { Owner = this };

        // Convidar a partir da tela de Equipe: a janela de convite abre por CIMA dela e, ao fechar,
        // a lista é RELIDA — sem isso, quem acabou de ser convidado só apareceria se o operador
        // descobrisse sozinho que precisa clicar em Atualizar.
        window.InviteRequested += (_, _) =>
        {
            ShowInviteWindow(team, TeamInviteMode.Generate, window);
            _ = teamViewModel.LoadAsync();
        };

        window.ShowDialog();
    }

    /// <summary>
    /// Abre a janela de time (modal) no modo pedido — fundar, convidar ou entrar — e, ao fechar,
    /// REAVALIA em qual cofre o operador está.
    ///
    /// <para>Sem essa reavaliação existe uma janela de falha muda concreta: gerar o primeiro convite
    /// é o ato que faz a chave do time NASCER neste PC, ou seja, é exatamente aí que o workspace
    /// ativo passa a ser "de time" — e o indicador continuaria dizendo "cofre pessoal", sem o aviso,
    /// até o próximo reinício. É o pior momento possível para o aviso sumir: o operador acabou de
    /// convidar alguém e vai começar a cadastrar achando que já está compartilhando.</para>
    ///
    /// <para><b>Criar o time NÃO troca o cofre desta sessão</b>, e o operador precisa saber disso
    /// antes de ir cadastrar. O cofre é decidido uma vez, no boot — trocá-lo com a UI viva exigiria
    /// trocar cofre, banco, store e todos os ViewModels ao mesmo tempo. Por isso o aviso aparece na
    /// hora: sem ele o operador cria o time, cadastra o cliente seguinte no cofre PESSOAL e só
    /// descobre semanas depois, quando o colega diz que não vê nada.</para>
    /// </summary>
    private void ShowInviteWindow(Account.TeamContext team, TeamInviteMode mode, Window owner)
    {
        var window = new TeamInviteWindow(new TeamInviteViewModel(team, mode))
        {
            Owner = owner,
        };
        window.ShowDialog();

        if (window.CreatedTeam)
        {
            // O texto sai da MESMA constante que as outras três telas usam (VaultSwitchText): as
            // quatro mandavam "feche e abra o RemoteOps", que era falso — o app entra pelo cache da
            // AMK e volta ao mesmo cofre. Quatro cópias eram quatro bugs esperando divergir.
            MessageBox.Show(
                owner,
                TeamInviteViewModel.TeamCreatedNotice,
                "RemoteOps — Time criado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // Barato: com a chave já no disco, a sondagem nem toca a rede. A regra em si mora no VM
        // (SettingsViewModel.RefreshVaultScopeAsync), onde é exercitada por teste.
        _ = Vm.RefreshVaultScopeAsync();
    }

    // Seleciona a aba pelo texto do Header (ex.: abrir direto em "Atualização" via o menu do avatar).
    private void SelectTabByHeader(string header)
    {
        foreach (object item in Tabs.Items)
        {
            if (item is System.Windows.Controls.TabItem tab && tab.Header as string == header)
            {
                tab.IsSelected = true;
                return;
            }
        }
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext;

    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // DataContext pode ainda ser null quando o SelectionChanged inicial dispara dentro de
        // InitializeComponent (antes do ctor setar o DataContext); o 'as' evita NRE.
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not System.Windows.Controls.TabItem tab)
        {
            return;
        }

        if (tab.Header as string == "Novidades")
        {
            (DataContext as SettingsViewModel)?.Changelog?.MarkAllSeen();
        }

        // Na aba Conta o único salvar é "Aplicar e reiniciar" (validado); esconde o "Salvar" global
        // para não haver dois botões de salvar ambíguos (um deles sem validação da URL). GlobalSaveBar
        // pode ser null no SelectionChanged inicial dentro de InitializeComponent.
        if (GlobalSaveBar is not null)
        {
            GlobalSaveBar.Visibility = tab.Header as string == "Conta"
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }
    }

    private void Preview_Expanded(object sender, RoutedEventArgs e)
        => (DataContext as SettingsViewModel)?.BugReport?.PreviewCommand.Execute(null);

    private void BrowseWinBox_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Selecionar o executável do WinBox",
            Filter = "WinBox (*.exe)|*.exe|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string sha = HashUtil.Sha256File(dlg.FileName);
            Vm.SetWinBox(dlg.FileName, sha);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Não foi possível ler o arquivo:\n{ex.Message}",
                "WinBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"Sem permissão para ler o arquivo:\n{ex.Message}",
                "WinBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RepinWinBox_Click(object sender, RoutedEventArgs e)
    {
        string? path = Vm.WinBoxExePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"O executável não foi encontrado em:\n{path}",
                "WinBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string sha = HashUtil.Sha256File(path);
            Vm.SetWinBox(path, sha);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"Não foi possível ler o arquivo:\n{ex.Message}",
                "WinBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"Sem permissão para ler o arquivo:\n{ex.Message}",
                "WinBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
