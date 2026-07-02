using System.Windows;
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
}
