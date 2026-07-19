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
