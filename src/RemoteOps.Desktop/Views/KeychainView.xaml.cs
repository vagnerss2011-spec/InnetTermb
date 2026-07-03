using System;
using System.Windows;
using System.Windows.Controls;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
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
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        if (dvm.IsKeyType)
        {
            if (!ValidateKey(dlg.ClassifyPrivateKey()))
            {
                return;
            }

            char[] key = dlg.GetPrivateKey();
            char[] passphrase = dlg.GetPassphrase();
            await Vm.CreateKeyAsync(dvm.Name, dvm.Username, key, passphrase.Length > 0 ? passphrase : null);
            if (passphrase.Length == 0)
            {
                Array.Clear(passphrase);
            }
        }
        else
        {
            await Vm.CreateAsync(dvm.Name, dvm.Username, dlg.GetPassword());
        }
    }

    private async void ReplaceKey_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCredential is not { Type: CredentialTypes.PrivateKey } cred)
        {
            return;
        }

        var dvm = new CredentialDialogViewModel(CredentialDialogMode.Add) { IsKeyType = true };
        var dlg = new CredentialDialog(dvm) { Owner = Window.GetWindow(this), Title = "Replace SSH key" };
        if (dlg.ShowDialog() != true || !ValidateKey(dlg.ClassifyPrivateKey()))
        {
            return;
        }

        await Vm.ReplaceKeyAsync(cred, dlg.GetPrivateKey());
    }

    private async void ChangePassphrase_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedCredential is not { Type: CredentialTypes.PrivateKey } cred)
        {
            return;
        }

        var dvm = new CredentialDialogViewModel(CredentialDialogMode.ChangePassword);
        var dlg = new CredentialDialog(dvm) { Owner = Window.GetWindow(this), Title = "Change passphrase" };
        if (dlg.ShowDialog() == true)
        {
            await Vm.ChangePassphraseAsync(cred, dlg.GetPassword());
        }
    }

    private bool ValidateKey(PrivateKeyKind kind)
    {
        switch (kind)
        {
            case PrivateKeyKind.PuttyPpk:
                MessageBox.Show(Window.GetWindow(this),
                    "Chave no formato PuTTY (.ppk). Converta no PuTTYgen: Conversions → Export OpenSSH key, e importe o arquivo gerado.",
                    "Chave SSH", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            case PrivateKeyKind.Invalid:
                MessageBox.Show(Window.GetWindow(this),
                    "Cole ou importe uma chave privada OpenSSH/PEM (começa com -----BEGIN …).",
                    "Chave SSH", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            default:
                return true;
        }
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
