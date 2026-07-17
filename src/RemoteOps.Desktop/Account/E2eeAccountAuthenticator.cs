using System.Net;
using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// Liga a UI de conta ao núcleo de cripto (<see cref="AccountKeyService"/>) e ao backend
/// (<see cref="IAccountApi"/>), sem inventar cripto nenhuma — só orquestra o fluxo da spec §6.
///
/// REGRA DE OURO: a senha não sai deste processo. No registro ela vira AuthHash + escrows; no login
/// ela vira AuthHash (prova) e KEK (abre a AMK LOCALMENTE, depois que o servidor devolve o blob).
/// O servidor nunca vê senha, MasterKey, KEK nem AMK.
/// </summary>
public sealed class E2eeAccountAuthenticator : IAccountAuthenticator
{
    /// <summary>Versão do esquema de embrulho da AMK (spec §4.2 <c>amk_key_version</c>).</summary>
    private const int AmkKeyVersionV1 = 1;

    /// <summary>
    /// Papel RBAC de quem cria a conta (backend: <c>Roles.Owner</c>). Só é usado como fallback se o
    /// servidor omitir a lista de workspaces no registro — o valor autoritativo vem sempre dele.
    /// </summary>
    private const string OwnerRole = "Owner";

    private readonly IAccountApi _api;
    private readonly AccountKeyService _keys = new();
    private readonly Guid _deviceId;
    private readonly string _deviceName;

    public E2eeAccountAuthenticator(IAccountApi api, Guid deviceId, string deviceName)
    {
        _api = api;
        _deviceId = deviceId;
        _deviceName = deviceName;
    }

    public async Task<AccountSession> RegisterAsync(
        string email, char[] password, string workspaceName, CancellationToken ct = default)
    {
        // Enroll faz tudo o que é sensível de uma vez (AMK + salt + recovery + os 2 escrows) e já
        // devolve a KEK zerada por dentro — nada de material de chave sobra aqui. Passa o char[] do
        // PasswordBox direto (sem virar string): o núcleo converte pra UTF-8 num buffer próprio e o
        // zera; o char[] em si é zerado pelo AccountViewModel. Quem chama Enroll NÃO zera o char[].
        AccountEnrollment enrollment = _keys.Enroll(password);

        var request = new RegisterAccountRequest(
            email,
            enrollment.Argon2Salt,
            enrollment.Params,
            enrollment.AuthHash,
            enrollment.WrappedAmkPwd,
            enrollment.WrappedAmkRec,
            AmkKeyVersionV1,
            _deviceId.ToString(),
            _deviceName,
            workspaceName);

        RegisterAccountResponse response;
        try
        {
            response = await _api.RegisterAsync(request, ct);
        }
        catch
        {
            // Registro falhou (rede/servidor/e-mail já existe): a AMK que acabamos de gerar não vale
            // mais nada e não pode ficar viva na memória enquanto o operador tenta de novo.
            CryptographicOperations.ZeroMemory(enrollment.Amk);
            throw;
        }

        return new AccountSession(
            email,
            // O workspaceId autoritativo do registro é este campo — não o primeiro item da lista.
            // É ELE que o backend acabou de criar; a lista existe só porque o registro reusa o
            // emissor de sessão do login.
            response.WorkspaceId,
            enrollment.Amk,
            new TokenSet(response.AccessToken, response.RefreshToken, response.ExpiresAt),
            response.Workspaces ?? [new AccountWorkspace(response.WorkspaceId, workspaceName, OwnerRole)],
            enrollment.RecoveryKey);
    }

    public async Task<AccountSession> LoginAsync(
        string email, char[] password, CancellationToken ct = default)
    {
        // 1) Salt/params da conta (públicos) — sem eles o device não consegue re-derivar a MasterKey.
        KdfResponse kdf = await _api.GetKdfAsync(email, ct);

        // 2) Prova de senha. A KEK derivada junto é descartada aqui: o unwrap acontece no passo 3,
        //    DEPOIS de o servidor devolver o escrow (que só existe se a prova passar). O char[] do
        //    PasswordBox entra direto no núcleo (sem virar string); ele é usado de novo no passo 3 e
        //    zerado uma única vez pelo AccountViewModel — por isso NÃO o zeramos aqui.
        AccountKeyMaterial material = _keys.DeriveFromPassword(password, kdf.Argon2Salt, kdf.Argon2Params);
        E2eeLoginResponse login;
        try
        {
            login = await _api.LoginAsync(
                new E2eeLoginRequest(email, material.AuthHash, _deviceId.ToString(), _deviceName), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Kek);
        }

        // Um 200 sem escrow/workspace é contrato quebrado do servidor (conta legada, pré-E2EE, não
        // chega aqui: o /auth/kdf devolve params decoy pra ela e o /auth/login recusa com 401). Sem
        // o escrow não há AMK — falhar explícito é melhor que seguir com um cofre que não abre.
        if (login.WrappedAmkPwd is not { Length: > 0 } wrappedAmkPwd
            || login.Workspaces is not { Count: > 0 } workspaces)
        {
            throw new CloudSyncException(HttpStatusCode.UnprocessableContent);
        }

        // 3) Desembrulha a AMK localmente. Chamar UnwrapAmkWithPassword (e não reaproveitar a KEK do
        //    passo 2 com a primitiva UnwrapKey) roda o Argon2id uma 2ª vez — ~centenas de ms, num
        //    fluxo que acontece uma vez por device. É o preço de NÃO duplicar aqui fora o AAD
        //    "amk|pwd|v1", que é privado do núcleo: uma constante de cripto copiada é exatamente o
        //    tipo de coisa que diverge em silêncio e só aparece como "cofre não abre" em campo.
        byte[] amk = _keys.UnwrapAmkWithPassword(password, kdf.Argon2Salt, kdf.Argon2Params, wrappedAmkPwd);

        return new AccountSession(
            email,
            // Fase 1 é mono-workspace (spec §11): o primeiro é O workspace da conta. Multi-workspace
            // na UI é fase seguinte — quando vier, a escolha passa a ser do operador.
            workspaces[0].Id,
            amk,
            new TokenSet(login.AccessToken, login.RefreshToken, login.ExpiresAt),
            workspaces);
    }
}
