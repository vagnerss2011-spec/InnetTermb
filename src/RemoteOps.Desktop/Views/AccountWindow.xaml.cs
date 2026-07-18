using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

/// <summary>
/// Janela "Entrar / Criar conta". Abrir com <see cref="Window.ShowDialog"/> (o padrão dos diálogos
/// do app): o <c>DialogResult</c> é o sinal de "autenticou" pra quem abriu.
///
/// A senha vive só aqui e no <c>char[]</c> que vai pro VM: nada de propriedade string bindada — um
/// binding de senha deixaria cópias imutáveis da senha do cofre na memória até o GC.
/// </summary>
public partial class AccountWindow : Window
{
    public AccountWindow(AccountViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Authenticated += (_, _) => { DialogResult = true; Close(); };
        // Fechou sem autenticar (X/Esc no meio do registro) → a AMK da sessão pendente não pode
        // ficar viva na memória do processo.
        Closed += (_, _) => { if (DialogResult != true) viewModel.ClearSession(); };
    }

    private AccountViewModel Vm => (AccountViewModel)DataContext;

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        char[] password = ReadSecure(PasswordField);
        // Confirmação só existe no registro; no login o campo está colapsado (e vazio).
        char[]? confirm = Vm.IsRegisterMode ? ReadSecure(ConfirmPasswordField) : null;

        // SubmitAsync zera os dois buffers — inclusive quando a validação falha antes da rede.
        await Vm.SubmitAsync(password, confirm);

        // Some com o texto dos campos também (o PasswordBox guarda a senha internamente). EXCETO no
        // desafio de 2FA: aí o reenvio (senha do PasswordBox + código) ainda precisa da senha; ela é
        // limpa quando o fluxo resolve (autenticou ou trocou de modo).
        if (!Vm.IsMfaChallenge)
        {
            PasswordField.Clear();
            ConfirmPasswordField.Clear();
        }
    }

    /// <summary>
    /// "Esqueci a senha": abre a recuperação (reusa o autenticador via <see cref="AccountViewModel.CreateRecoveryFlow"/>)
    /// como diálogo modal. Voltou com sucesso → recado no login, com a senha nova já valendo.
    /// </summary>
    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        var recovery = new PasswordRecoveryWindow(Vm.CreateRecoveryFlow()) { Owner = this };
        if (recovery.ShowDialog() == true)
        {
            Vm.NotifyPasswordReset();
        }
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
