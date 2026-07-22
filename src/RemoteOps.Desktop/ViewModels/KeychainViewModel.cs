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
    private readonly string _storeWorkspaceId;
    private readonly string _vaultWorkspaceId;
    private CredentialRef? _selected;
    private string _errorMessage = string.Empty;

    /// <param name="storeWorkspaceId">
    /// Escopo das ENTIDADES (<c>assets.workspace_id</c>): fica <c>"ws-local"</c> nos dois bancos,
    /// porque dono e colega precisam consultar a lista com a MESMA string dentro do banco do time.
    /// </param>
    /// <param name="vaultWorkspaceId">
    /// Identidade do COFRE onde o envelope da senha é selado: <c>"ws-local"</c> no pessoal,
    /// <c>"time:{W}"</c> no do time. É OUTRO eixo — conflatá-lo com o de cima faria a senha do
    /// cliente do time nascer no cofre pessoal do operador, sem erro nenhum na tela.
    /// </param>
    public KeychainViewModel(
        ILocalStore store, IVault vault, string storeWorkspaceId, string vaultWorkspaceId)
    {
        _store = store;
        _vault = vault;
        _storeWorkspaceId = storeWorkspaceId;
        _vaultWorkspaceId = vaultWorkspaceId;
    }

    public ObservableCollection<CredentialRef> Credentials { get; } = [];

    public CredentialRef? SelectedCredential
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>
    /// ⚠️ <b>A recusa do cofre, em pt-BR, ONDE o operador está.</b>
    ///
    /// <para>Num cofre de time sem a chave o chaveiro recusa ALTO — de propósito — com uma
    /// <see cref="VaultException"/> cuja mensagem já é acionável ("aceite o convite … antes de
    /// cadastrar ou abrir senhas do time"). Sem esta propriedade ela subia até o
    /// <c>DispatcherUnhandledException</c> do <c>App</c> e virava uma caixa intitulada <i>"Erro
    /// inesperado"</i>: a frase útil continuava lá dentro, mas emoldurada como defeito do programa.
    /// O operador então repete a operação, ou liga para o suporte — em vez de aceitar o convite.</para>
    ///
    /// <para>A guarda não muda em nada: o que muda é o lugar onde a recusa aparece.</para>
    /// </summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { Set(ref _errorMessage, value); RaisePropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async Task LoadAsync()
    {
        Credentials.Clear();
        foreach (var c in await _store.GetCredentialRefsAsync(_storeWorkspaceId))
            Credentials.Add(c);
    }

    public Task CreateAsync(string name, string username, char[] password) => GuardAsync(
        NotStored,
        async () =>
        {
            string credId = Guid.NewGuid().ToString("n");
            try
            {
                SecretEnvelope env = await _vault.StoreAsync(
                    new VaultStoreRequest { WorkspaceId = _vaultWorkspaceId, CredentialId = credId, Type = CredentialTypes.Password, ActorUserId = Actor },
                    password);
                await _store.AddCredentialRefAsync(new CredentialRef
                {
                    Id = credId,
                    Name = name.Trim(),
                    Type = CredentialTypes.Password,
                    Metadata = new CredentialMetadata { Username = username.Trim() },
                    SecretEnvelopeId = env.EnvelopeId,
                });
            }
            finally
            {
                // No finally, e não depois da chamada: com o cofre recusando (fail-closed do time),
                // a linha de baixo nunca era alcançada e a senha em claro ficava na heap esperando o
                // GC. Zerar é obrigação do caminho de ERRO tanto quanto do de sucesso.
                Array.Clear(password);
            }
        });

    /// <summary>Cria credencial de chave privada: envelope da chave + (opcional) envelope da passphrase.</summary>
    public Task CreateKeyAsync(string name, string username, char[] privateKey, char[]? passphrase) => GuardAsync(
        NotStored,
        async () =>
        {
            string credId = Guid.NewGuid().ToString("n");
            try
            {
                SecretEnvelope keyEnv = await _vault.StoreAsync(
                    new VaultStoreRequest { WorkspaceId = _vaultWorkspaceId, CredentialId = credId, Type = CredentialTypes.PrivateKey, ActorUserId = Actor },
                    privateKey);

                string? passphraseEnvelopeId = null;
                if (passphrase is { Length: > 0 })
                {
                    SecretEnvelope ppEnv = await _vault.StoreAsync(
                        new VaultStoreRequest { WorkspaceId = _vaultWorkspaceId, CredentialId = credId + "-pp", Type = CredentialTypes.PrivateKeyPassphrase, ActorUserId = Actor },
                        passphrase);
                    passphraseEnvelopeId = ppEnv.EnvelopeId;
                }

                await _store.AddCredentialRefAsync(new CredentialRef
                {
                    Id = credId,
                    Name = name.Trim(),
                    Type = CredentialTypes.PrivateKey,
                    Metadata = new CredentialMetadata { Username = username.Trim(), HasPrivateKey = true, PassphraseEnvelopeId = passphraseEnvelopeId },
                    SecretEnvelopeId = keyEnv.EnvelopeId,
                });
            }
            finally
            {
                Array.Clear(privateKey);
                if (passphrase is not null)
                {
                    Array.Clear(passphrase);
                }
            }
        });

    /// <summary>Substitui a chave privada (rotaciona o envelope da chave).</summary>
    public Task ReplaceKeyAsync(CredentialRef cred, char[] newKey) => GuardAsync(
        NotStored,
        async () =>
        {
            try
            {
                if (cred.SecretEnvelopeId is { } envId)
                {
                    SecretEnvelope rotated = await _vault.RotateAsync(envId, newKey, new VaultAccessContext { ActorUserId = Actor });
                    await _store.UpdateCredentialRefAsync(Repoint(cred, rotated.EnvelopeId, cred.Metadata?.PassphraseEnvelopeId));
                }
            }
            finally
            {
                Array.Clear(newKey);
            }
        });

    /// <summary>Troca a passphrase da chave: rotaciona o envelope se já existir, senão cria um novo.</summary>
    public Task ChangePassphraseAsync(CredentialRef cred, char[] newPassphrase) => GuardAsync(
        NotStored,
        async () =>
        {
            try
            {
                if (cred.Metadata?.PassphraseEnvelopeId is { } ppId)
                {
                    SecretEnvelope rotated = await _vault.RotateAsync(ppId, newPassphrase, new VaultAccessContext { ActorUserId = Actor });
                    await _store.UpdateCredentialRefAsync(Repoint(cred, cred.SecretEnvelopeId, rotated.EnvelopeId));
                }
                else
                {
                    SecretEnvelope ppEnv = await _vault.StoreAsync(
                        new VaultStoreRequest { WorkspaceId = _vaultWorkspaceId, CredentialId = cred.Id + "-pp", Type = CredentialTypes.PrivateKeyPassphrase, ActorUserId = Actor },
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
            }
            finally
            {
                Array.Clear(newPassphrase);
            }
        });

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

    public Task ChangePasswordAsync(CredentialRef cred, char[] newPassword) => GuardAsync(
        NotStored,
        async () =>
        {
            try
            {
                // Só credencial de senha rotaciona por aqui — chave usa ReplaceKey/ChangePassphrase.
                if (cred.Type == CredentialTypes.Password && cred.SecretEnvelopeId is { } envId)
                {
                    SecretEnvelope rotated = await _vault.RotateAsync(envId, newPassword, new VaultAccessContext { ActorUserId = Actor });
                    await _store.UpdateCredentialRefAsync(Repoint(cred, rotated.EnvelopeId, cred.Metadata?.PassphraseEnvelopeId));
                }
            }
            finally
            {
                Array.Clear(newPassword);
            }
        });

    public Task DeleteAsync(CredentialRef cred) => GuardAsync(
        NotDeleted,
        async () =>
        {
            if (cred.SecretEnvelopeId is { } envId)
                await _vault.RevokeAsync(envId, new VaultAccessContext { ActorUserId = Actor });
            if (cred.Metadata?.PassphraseEnvelopeId is { } ppId)
                await _vault.RevokeAsync(ppId, new VaultAccessContext { ActorUserId = Actor });
            await _store.DeleteCredentialRefAsync(cred.Id);
        });

    /// <summary>Lead-in de quando a GRAVAÇÃO no cofre não aconteceu.</summary>
    private const string NotStored = "A senha não foi gravada e nada mudou no cofre.";

    /// <summary>Lead-in de quando a EXCLUSÃO não aconteceu (a credencial continua na lista).</summary>
    private const string NotDeleted = "A credencial não foi excluída e nada mudou no cofre.";

    /// <summary>
    /// Roda a operação, recarrega a lista e — o ponto deste método — transforma a recusa do cofre em
    /// TEXTO na tela em vez de deixá-la subir.
    ///
    /// <para><b>Só <see cref="VaultException"/> é capturada.</b> Ela é a recusa DELIBERADA do cofre,
    /// com frase escrita para o operador. Qualquer outra exceção (banco, disco, bug) continua subindo
    /// para o tratador global: engolir tudo aqui trocaria uma caixa de erro por um app que não faz
    /// nada e não diz nada, que é o defeito que esta fatia inteira combate.</para>
    ///
    /// <para>O sucesso LIMPA o recado anterior: erro que não some é indistinguível de erro novo, e é
    /// assim que o operador aprende a ignorá-los.</para>
    /// </summary>
    private async Task GuardAsync(string leadIn, Func<Task> operacao)
    {
        try
        {
            await operacao();
        }
        catch (VaultException ex)
        {
            // A mensagem do cofre é PRESERVADA inteira: é ela que diz o que fazer ("aceite o convite
            // … antes de cadastrar ou abrir senhas do time"). O lead-in só acrescenta o que o
            // operador não tem como deduzir — se sobrou alguma coisa meio-feita.
            ErrorMessage = $"{leadIn} {ex.Message}";
            return;
        }

        ErrorMessage = string.Empty;
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
