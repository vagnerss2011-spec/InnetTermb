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
            new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId, Type = CredentialTypes.Password, ActorUserId = Actor },
            password);
        Array.Clear(password);
        await _store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = name.Trim(),
            Type = CredentialTypes.Password,
            Metadata = new CredentialMetadata { Username = username.Trim() },
            SecretEnvelopeId = env.EnvelopeId,
        });
        await LoadAsync();
    }

    /// <summary>Cria credencial de chave privada: envelope da chave + (opcional) envelope da passphrase.</summary>
    public async Task CreateKeyAsync(string name, string username, char[] privateKey, char[]? passphrase)
    {
        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope keyEnv = await _vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId, Type = CredentialTypes.PrivateKey, ActorUserId = Actor },
            privateKey);
        Array.Clear(privateKey);

        string? passphraseEnvelopeId = null;
        if (passphrase is { Length: > 0 })
        {
            SecretEnvelope ppEnv = await _vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId + "-pp", Type = CredentialTypes.PrivateKeyPassphrase, ActorUserId = Actor },
                passphrase);
            passphraseEnvelopeId = ppEnv.EnvelopeId;
        }
        if (passphrase is not null)
        {
            Array.Clear(passphrase);
        }

        await _store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = name.Trim(),
            Type = CredentialTypes.PrivateKey,
            Metadata = new CredentialMetadata { Username = username.Trim(), HasPrivateKey = true, PassphraseEnvelopeId = passphraseEnvelopeId },
            SecretEnvelopeId = keyEnv.EnvelopeId,
        });
        await LoadAsync();
    }

    /// <summary>Substitui a chave privada (rotaciona o envelope da chave).</summary>
    public async Task ReplaceKeyAsync(CredentialRef cred, char[] newKey)
    {
        if (cred.SecretEnvelopeId is { } envId)
        {
            SecretEnvelope rotated = await _vault.RotateAsync(envId, newKey, new VaultAccessContext { ActorUserId = Actor });
            await _store.UpdateCredentialRefAsync(Repoint(cred, rotated.EnvelopeId, cred.Metadata?.PassphraseEnvelopeId));
        }
        Array.Clear(newKey);
        await LoadAsync();
    }

    /// <summary>Troca a passphrase da chave: rotaciona o envelope se já existir, senão cria um novo.</summary>
    public async Task ChangePassphraseAsync(CredentialRef cred, char[] newPassphrase)
    {
        if (cred.Metadata?.PassphraseEnvelopeId is { } ppId)
        {
            SecretEnvelope rotated = await _vault.RotateAsync(ppId, newPassphrase, new VaultAccessContext { ActorUserId = Actor });
            await _store.UpdateCredentialRefAsync(Repoint(cred, cred.SecretEnvelopeId, rotated.EnvelopeId));
        }
        else
        {
            SecretEnvelope ppEnv = await _vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = cred.Id + "-pp", Type = CredentialTypes.PrivateKeyPassphrase, ActorUserId = Actor },
                newPassphrase);
            await _store.UpdateCredentialRefAsync(new CredentialRef
            {
                Id = cred.Id,
                Name = cred.Name,
                Type = cred.Type,
                Scope = cred.Scope,
                Metadata = new CredentialMetadata { Username = cred.Metadata?.Username, HasPrivateKey = true, PassphraseEnvelopeId = ppEnv.EnvelopeId },
                SecretEnvelopeId = cred.SecretEnvelopeId,
                Version = cred.Version,
            });
        }
        Array.Clear(newPassphrase);
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
        // Só credencial de senha rotaciona por aqui — chave usa ReplaceKey/ChangePassphrase.
        if (cred.Type == CredentialTypes.Password && cred.SecretEnvelopeId is { } envId)
        {
            SecretEnvelope rotated = await _vault.RotateAsync(envId, newPassword, new VaultAccessContext { ActorUserId = Actor });
            await _store.UpdateCredentialRefAsync(Repoint(cred, rotated.EnvelopeId, cred.Metadata?.PassphraseEnvelopeId));
        }

        Array.Clear(newPassword);
        await LoadAsync();
    }

    public async Task DeleteAsync(CredentialRef cred)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RevokeAsync(envId, new VaultAccessContext { ActorUserId = Actor });
        if (cred.Metadata?.PassphraseEnvelopeId is { } ppId)
            await _vault.RevokeAsync(ppId, new VaultAccessContext { ActorUserId = Actor });
        await _store.DeleteCredentialRefAsync(cred.Id);
        await LoadAsync();
    }

    /// <summary>
    /// Reaponta o <see cref="CredentialRef"/> para os envelopes VIVOS, preservando o resto dos
    /// metadados.
    ///
    /// <para><b>Por que existe:</b> <c>RotateAsync</c> cria um envelope com ID NOVO e tombstoneia o
    /// antigo. Sem repontar, o ref fica apontando pro tombstone e o estrago é duplo: conectar falha
    /// NESTE PC ("Envelope revogado") e, como nenhum metadado muda, nada entra no outbox — a troca
    /// nunca chega ao outro device. O <c>UpdateCredentialRefAsync</c> conserta as duas coisas de
    /// uma vez.</para>
    ///
    /// <para><see cref="CredentialRef"/>/<see cref="CredentialMetadata"/> são classes com <c>init</c>
    /// (não são <c>record</c>), então não há <c>with</c>: a cópia é campo a campo, como o restante
    /// deste arquivo já faz.</para>
    /// </summary>
    private static CredentialRef Repoint(CredentialRef cred, string? secretEnvelopeId, string? passphraseEnvelopeId) => new()
    {
        Id = cred.Id,
        Name = cred.Name,
        Type = cred.Type,
        Scope = cred.Scope,
        Metadata = new CredentialMetadata
        {
            Username = cred.Metadata?.Username,
            HasPrivateKey = cred.Metadata?.HasPrivateKey ?? false,
            PassphraseEnvelopeId = passphraseEnvelopeId,
            LastRotatedAt = cred.Metadata?.LastRotatedAt,
        },
        SecretEnvelopeId = secretEnvelopeId,
        Version = cred.Version,
    };
}
