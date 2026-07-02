using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => Close();
    }

    private SettingsViewModel Vm => (SettingsViewModel)DataContext;

    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // DataContext pode ainda ser null quando o SelectionChanged inicial dispara dentro de
        // InitializeComponent (antes do ctor setar o DataContext); o 'as' evita NRE.
        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is System.Windows.Controls.TabItem { Header: "Novidades" })
        {
            (DataContext as SettingsViewModel)?.Changelog?.MarkAllSeen();
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
