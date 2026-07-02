using System.Windows;
using System.Windows.Controls;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class KeychainView : UserControl
{
    public KeychainView()
    {
        InitializeComponent();
    }

    private KeychainViewModel Vm => (KeychainViewModel)DataContext;

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dvm = new CredentialDialogViewModel(CredentialDialogMode.Add);
        var dlg = new CredentialDialog(dvm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            await Vm.CreateAsync(dvm.Name, dvm.Username, dlg.GetPassword());
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCredential is not { } cred) return;
        var dvm = new CredentialDialogViewModel(CredentialDialogMode.Edit, cred.Name, cred.Metadata?.Username ?? string.Empty);
        var dlg = new CredentialDialog(dvm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            await Vm.UpdateAsync(cred, dvm.Name, dvm.Username);
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCredential is not { } cred) return;
        var dvm = new CredentialDialogViewModel(CredentialDialogMode.ChangePassword);
        var dlg = new CredentialDialog(dvm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
            await Vm.ChangePasswordAsync(cred, dlg.GetPassword());
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCredential is not { } cred) return;
        var confirm = MessageBox.Show(
            Window.GetWindow(this), $"Delete credential “{cred.Name}”? This revokes the stored secret.",
            "Delete credential", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
            await Vm.DeleteAsync(cred);
    }
}
