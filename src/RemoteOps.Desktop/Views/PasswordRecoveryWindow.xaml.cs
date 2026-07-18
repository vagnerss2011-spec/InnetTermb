using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// "Esqueci a senha" (Fase 4). Abrir com <see cref="Window.ShowDialog"/>: <c>DialogResult == true</c>
/// sinaliza "senha redefinida" a quem abriu (a AccountWindow mostra o recado no login).
///
/// A nova senha vive só aqui e no <c>char[]</c> que vai pro VM — nada de propriedade string bindada,
/// mesmo padrão da AccountWindow.
/// </summary>
public partial class PasswordRecoveryWindow : Window
{
    public PasswordRecoveryWindow(PasswordRecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ResetCompleted += (_, _) => { DialogResult = true; Close(); };
        viewModel.BackToLogin += (_, _) => { DialogResult = false; Close(); };
    }

    private PasswordRecoveryViewModel Vm => (PasswordRecoveryViewModel)DataContext;

    private async void Request_Click(object sender, RoutedEventArgs e)
        => await Vm.RequestResetAsync();

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        char[] password = ReadSecure(NewPasswordField);
        char[] confirm = ReadSecure(ConfirmPasswordField);

        // SubmitResetAsync zera os dois buffers — inclusive quando a validação falha antes da rede.
        await Vm.SubmitResetAsync(password, confirm);

        // Some com o texto dos campos também (o PasswordBox guarda a senha internamente).
        NewPasswordField.Clear();
        ConfirmPasswordField.Clear();
    }

    /// <summary>Lê a senha como char[] via SecureString/BSTR (nunca retorna string). O VM zera depois.</summary>
    private static char[] ReadSecure(PasswordBox box)
    {
        var secure = box.SecurePassword;
        var chars = new char[secure.Length];
        nint bstr = Marshal.SecureStringToBSTR(secure);
        try { Marshal.Copy(bstr, chars, 0, chars.Length); }
        finally { Marshal.ZeroFreeBSTR(bstr); }
        return chars;
    }
}
