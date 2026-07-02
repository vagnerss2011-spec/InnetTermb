using System;

namespace RemoteOps.Desktop.ViewModels;

public enum CredentialDialogMode { Add, Edit, ChangePassword }

/// <summary>ViewModel do diálogo de credencial (Add / Edit / Change password). A senha em si
/// NÃO passa por aqui — é lida do PasswordBox no code-behind (nunca em prop bindável).</summary>
public sealed class CredentialDialogViewModel : BaseViewModel
{
    private string _name = string.Empty;
    private string _username = string.Empty;

    public CredentialDialogViewModel(CredentialDialogMode mode, string name = "", string username = "")
    {
        Mode = mode;
        _name = name;
        _username = username;
        SaveCommand = new RelayCommand(
            () => Saved?.Invoke(this, EventArgs.Empty),
            () => Mode == CredentialDialogMode.ChangePassword || !string.IsNullOrWhiteSpace(Name));
    }

    public CredentialDialogMode Mode { get; }

    public string Title => Mode switch
    {
        CredentialDialogMode.Add => "Add credential",
        CredentialDialogMode.Edit => "Edit credential",
        CredentialDialogMode.ChangePassword => "Change password",
        _ => "Credential",
    };

    public bool ShowNameUsername => Mode != CredentialDialogMode.ChangePassword;
    public bool ShowPassword => Mode != CredentialDialogMode.Edit;

    public string Name { get => _name; set { Set(ref _name, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string Username { get => _username; set => Set(ref _username, value); }

    public RelayCommand SaveCommand { get; }
    public event EventHandler? Saved;
}
