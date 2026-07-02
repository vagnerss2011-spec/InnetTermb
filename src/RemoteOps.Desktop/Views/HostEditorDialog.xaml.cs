using System.Windows;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class HostEditorDialog : Window
{
    public HostEditorDialog(HostEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => { DialogResult = true; Close(); };
    }
}
