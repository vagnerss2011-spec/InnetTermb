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
    public char[] GetPassword() => ReadSecure(PasswordField);

    /// <summary>Passphrase da chave como char[] (o chamador zera).</summary>
    public char[] GetPassphrase() => ReadSecure(PassphraseField);

    /// <summary>Chave privada digitada/colada como char[] (o chamador zera).</summary>
    public char[] GetPrivateKey() => PrivateKeyField.Text.ToCharArray();

    /// <summary>Classificação da chave digitada (validação antes de salvar).</summary>
    public Infrastructure.PrivateKeyKind ClassifyPrivateKey()
        => Infrastructure.PrivateKeyInput.Classify(PrivateKeyField.Text);

    private static char[] ReadSecure(System.Windows.Controls.PasswordBox box)
    {
        var secure = box.SecurePassword;
        var chars = new char[secure.Length];
        nint bstr = Marshal.SecureStringToBSTR(secure);
        try { Marshal.Copy(bstr, chars, 0, chars.Length); }
        finally { Marshal.ZeroFreeBSTR(bstr); }
        return chars;
    }

    private void TypePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is CredentialDialogViewModel vm && sender is System.Windows.Controls.ComboBox cb)
            vm.IsKeyType = cb.SelectedIndex == 1;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar chave privada",
            Filter = "Chaves (*.pem;*.key;*.openssh)|*.pem;*.key;*.openssh|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            PrivateKeyField.Text = System.IO.File.ReadAllText(dlg.FileName);
    }
}
