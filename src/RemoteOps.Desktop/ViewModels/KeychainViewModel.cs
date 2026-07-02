using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Security.Vault;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Gerencia as credenciais do workspace: cria/edita/rotaciona/exclui via o cofre
/// (envelope encryption) e o store local (metadados). Nunca exibe o segredo — a senha
/// entra por PasswordBox (char[]) e é zerada após guardar.
/// </summary>
public sealed class KeychainViewModel : BaseViewModel
{
    private const string Actor = "local-user";
    private readonly ILocalStore _store;
    private readonly IVault _vault;
    private readonly string _workspaceId;
    private CredentialRef? _selected;

    public KeychainViewModel(ILocalStore store, IVault vault, string workspaceId)
    {
        _store = store;
        _vault = vault;
        _workspaceId = workspaceId;
    }

    public ObservableCollection<CredentialRef> Credentials { get; } = [];

    public CredentialRef? SelectedCredential
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public async Task LoadAsync()
    {
        Credentials.Clear();
        foreach (var c in await _store.GetCredentialRefsAsync(_workspaceId))
            Credentials.Add(c);
    }

    public async Task CreateAsync(string name, string username, char[] password)
    {
        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope env = await _vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId, Type = "password", ActorUserId = Actor },
            password);
        Array.Clear(password);
        await _store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = name.Trim(),
            Type = "password",
            Metadata = new CredentialMetadata { Username = username.Trim() },
            SecretEnvelopeId = env.EnvelopeId,
        });
        await LoadAsync();
    }

    public async Task UpdateAsync(CredentialRef cred, string name, string username)
    {
        await _store.UpdateCredentialRefAsync(new CredentialRef
        {
            Id = cred.Id,
            Name = name.Trim(),
            Type = cred.Type,
            Scope = cred.Scope,
            Metadata = new CredentialMetadata { Username = username.Trim(), HasPrivateKey = cred.Metadata?.HasPrivateKey ?? false },
            SecretEnvelopeId = cred.SecretEnvelopeId,
            Version = cred.Version,
        });
        await LoadAsync();
    }

    public async Task ChangePasswordAsync(CredentialRef cred, char[] newPassword)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RotateAsync(envId, newPassword, new VaultAccessContext { ActorUserId = Actor });
        Array.Clear(newPassword);
    }

    public async Task DeleteAsync(CredentialRef cred)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RevokeAsync(envId, new VaultAccessContext { ActorUserId = Actor });
        await _store.DeleteCredentialRefAsync(cred.Id);
        await LoadAsync();
    }
}
