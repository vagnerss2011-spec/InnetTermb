using System.Windows;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class NewGroupDialog : Window
{
    public NewGroupDialog(NewGroupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => { DialogResult = true; Close(); };
        Loaded += (_, _) => NameBox.Focus();
    }
}
