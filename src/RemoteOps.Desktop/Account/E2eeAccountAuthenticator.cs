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
        // devolve a KEK zerada por dentro — nada de material de chave sobra aqui.
        AccountEnrollment enrollment = _keys.Enroll(ToPasswordString(password));

        var request = new RegisterAccountRequest(
            email,
            enrollment.Argon2Salt,
            enrollment.Params,
            enrollment.AuthHash,
            enrollment.WrappedAmkPwd,
            enrollment.WrappedAmkRec,
            AmkKeyVersionV1,
            new FirstWorkspaceRequest(workspaceName));

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
            enrollment.Amk,
            new TokenSet(response.AccessToken, response.RefreshToken, response.ExpiresAt),
            response.Workspaces,
            enrollment.RecoveryKey);
    }

    public async Task<AccountSession> LoginAsync(
        string email, char[] password, CancellationToken ct = default)
    {
        string pwd = ToPasswordString(password);

        // 1) Salt/params da conta (públicos) — sem eles o device não consegue re-derivar a MasterKey.
        KdfResponse kdf = await _api.GetKdfAsync(email, ct);

        // 2) Prova de senha. A KEK derivada junto é descartada aqui: o unwrap acontece no passo 3,
        //    DEPOIS de o servidor devolver o escrow (que só existe se a prova passar).
        AccountKeyMaterial material = _keys.DeriveFromPassword(pwd, kdf.Argon2Salt, kdf.Argon2Params);
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

        // 3) Desembrulha a AMK localmente. Chamar UnwrapAmkWithPassword (e não reaproveitar a KEK do
        //    passo 2 com a primitiva UnwrapKey) roda o Argon2id uma 2ª vez — ~centenas de ms, num
        //    fluxo que acontece uma vez por device. É o preço de NÃO duplicar aqui fora o AAD
        //    "amk|pwd|v1", que é privado do núcleo: uma constante de cripto copiada é exatamente o
        //    tipo de coisa que diverge em silêncio e só aparece como "cofre não abre" em campo.
        byte[] amk = _keys.UnwrapAmkWithPassword(pwd, kdf.Argon2Salt, kdf.Argon2Params, login.WrappedAmkPwd);

        return new AccountSession(
            email,
            amk,
            new TokenSet(login.AccessToken, login.RefreshToken, login.ExpiresAt),
            login.Workspaces);
    }

    /// <summary>
    /// Ponto ÚNICO onde a senha vira string. O <see cref="AccountKeyService"/> (núcleo já provado,
    /// que não alteramos nesta task) recebe <c>string</c>; string é imutável, então esta cópia não
    /// pode ser zerada e vive até o GC coletá-la. O <c>char[]</c> vindo do PasswordBox continua
    /// sendo zerado pelo ViewModel. Pendência registrada no resumo da T7: um overload
    /// <c>DeriveFromPassword(char[] …)</c>/<c>Enroll(char[] …)</c> no núcleo eliminaria esta cópia.
    /// </summary>
    private static string ToPasswordString(char[] password) => new(password);
}
