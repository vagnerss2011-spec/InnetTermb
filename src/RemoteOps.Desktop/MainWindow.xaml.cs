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
    private double _sidebarMinWidth = 150;
    private GridLength _inspectorWidth = new(260);
    private double _inspectorMinWidth = 200;

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
        // O Checked de um MenuItem IsChecked="True" dispara DURANTE o InitializeComponent,
        // antes do campo nomeado SidebarColumn existir. Ignora até a árvore estar montada.
        if (SidebarColumn is null)
        {
            return;
        }

        if (((MenuItem)sender).IsChecked)
        {
            SidebarColumn.MinWidth = _sidebarMinWidth;
            SidebarColumn.Width = _sidebarWidth;
        }
        else
        {
            // MinWidth também precisa ir a 0: o Grid do WPF clampa a largura renderizada
            // até MinWidth, então zerar só Width encolheria a coluna a 150px em vez de escondê-la.
            _sidebarWidth = SidebarColumn.Width;
            _sidebarMinWidth = SidebarColumn.MinWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }
    }

    private void ToggleInspector_Changed(object sender, RoutedEventArgs e)
    {
        if (InspectorColumn is null)
        {
            return;
        }

        if (((MenuItem)sender).IsChecked)
        {
            InspectorColumn.MinWidth = _inspectorMinWidth;
            InspectorColumn.Width = _inspectorWidth;
        }
        else
        {
            _inspectorWidth = InspectorColumn.Width;
            _inspectorMinWidth = InspectorColumn.MinWidth;
            InspectorColumn.MinWidth = 0;
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
