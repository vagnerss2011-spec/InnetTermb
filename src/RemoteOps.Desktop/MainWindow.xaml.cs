using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;

namespace RemoteOps.Desktop;

public partial class MainWindow : Window
{
    private GridLength _sidebarWidth = new(220);
    private GridLength _inspectorWidth = new(260);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleSidebar_Changed(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).IsChecked)
        {
            SidebarColumn.Width = _sidebarWidth;
        }
        else
        {
            _sidebarWidth = SidebarColumn.Width;
            SidebarColumn.Width = new GridLength(0);
        }
    }

    private void ToggleInspector_Changed(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).IsChecked)
        {
            InspectorColumn.Width = _inspectorWidth;
        }
        else
        {
            _inspectorWidth = InspectorColumn.Width;
            InspectorColumn.Width = new GridLength(0);
        }
    }

    private void FocusSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
    }

    private void Docs_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://github.com/") { UseShellExecute = true });

    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, Vm.AppVersionText, "Sobre o RemoteOps", MessageBoxButton.OK, MessageBoxImage.Information);
}
