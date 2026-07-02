using System.Runtime.InteropServices;
using System.Windows;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class CredentialDialog : Window
{
    public CredentialDialog(CredentialDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => { DialogResult = true; Close(); };
    }

    /// <summary>Lê a senha digitada como char[] (o chamador zera após usar). Nunca retorna string.</summary>
    public char[] GetPassword()
    {
        var secure = PasswordField.SecurePassword;
        var chars = new char[secure.Length];
        nint bstr = Marshal.SecureStringToBSTR(secure);
        try { Marshal.Copy(bstr, chars, 0, chars.Length); }
        finally { Marshal.ZeroFreeBSTR(bstr); }
        return chars;
    }
}
