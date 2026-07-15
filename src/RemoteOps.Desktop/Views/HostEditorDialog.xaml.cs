using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class HostEditorDialog : Window
{
    public HostEditorDialog(HostEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => { DialogResult = true; Close(); };
        Loaded += async (_, _) => await viewModel.LoadCredentialsAsync();
        // Fechou sem salvar (Cancelar/X) → zera qualquer senha inline que ficou em rascunho.
        Closed += (_, _) => { if (DialogResult != true) viewModel.ClearInlineDrafts(); };
        // Avisa o VM se o PasswordBox inline tem senha (só o COMPRIMENTO, nunca o valor) — o botão
        // "Adicionar" no modo inline só habilita com usuário E senha preenchidos.
        InlinePasswordField.PasswordChanged += (_, _) =>
        {
            if (DataContext is HostEditorViewModel vm)
            {
                vm.HasInlinePassword = InlinePasswordField.SecurePassword.Length > 0;
            }
        };
    }

    private HostEditorViewModel Vm => (HostEditorViewModel)DataContext;

    // "Adicionar endpoint": no modo inline, lê a senha do PasswordBox como char[] e entrega ao VM
    // (que a materializa no cofre só no Salvar). No modo Keychain, usa o dropdown como antes.
    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.UseInlineCredential)
        {
            char[] password = ReadSecure(InlinePasswordField);
            Vm.AddInlineEndpoint(password);
            InlinePasswordField.Clear();
        }
        else
        {
            Vm.AddEndpoint();
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
