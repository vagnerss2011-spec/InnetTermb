using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Cloud;

namespace RemoteOps.UnitTests.Integration;

/// <summary>
/// Uma conta E2EE criada no SERVIDOR REAL (<c>POST /auth/register</c> pelo pipeline ASP.NET inteiro),
/// mais a AMK que os dois PCs do operador compartilham.
///
/// <para><b>Por que passar pelo HTTP e não semear o banco:</b> o que quebrou em produção mora
/// justamente aqui — rota, binding do JSON, emissão/validação do JWT e o header <c>X-Device-Id</c>.
/// Um seed direto no <c>AppDbContext</c> pularia essa camada inteira e o teste voltaria a provar só o
/// que já estava provado.</para>
///
/// <para>A cripto é a de verdade (<see cref="AccountKeyService"/>): a senha da conta deriva o AuthHash
/// que sobe e a KEK que embrulha a AMK. O servidor recebe prova e escrow opaco — nunca a senha.</para>
/// </summary>
internal sealed class CloudAccountFixture
{
    private readonly string _email;
    private readonly string _authHashBase64;

    private CloudAccountFixture(
        string email,
        string authHashBase64,
        byte[] amk,
        string serverWorkspaceId,
        TokenSet primaryTokens,
        Guid primaryDeviceId)
    {
        _email = email;
        _authHashBase64 = authHashBase64;
        Amk = amk;
        ServerWorkspaceId = serverWorkspaceId;
        PrimaryTokens = primaryTokens;
        PrimaryDeviceId = primaryDeviceId;
    }

    /// <summary>
    /// A raiz portável do cofre. Os dois devices recebem ESTA MESMA AMK — é a condição sem a qual o
    /// segredo selado no A não abriria no B (o <see cref="AmkWorkspaceKeyRing"/> guarda uma cópia, o
    /// array aqui continua íntegro).
    /// </summary>
    public byte[] Amk { get; }

    /// <summary>GUID do workspace no servidor — o que viaja no fio (distinto do workspace do cofre).</summary>
    public string ServerWorkspaceId { get; }

    /// <summary>Sessão do device que CRIOU a conta (o "PC A"), emitida pelo próprio /auth/register.</summary>
    public TokenSet PrimaryTokens { get; }

    public Guid PrimaryDeviceId { get; }

    public static async Task<CloudAccountFixture> EnrollAsync(CloudApiFactory factory, string accountPassword)
    {
        using HttpClient http = factory.CreateClient();

        var keys = new AccountKeyService();
        AccountEnrollment enrollment = keys.Enroll(accountPassword);

        // E-mail único por fixture: a API recusa e-mail repetido com 409, e os testes rodam em
        // paralelo no mesmo processo.
        string email = $"operador-{Guid.NewGuid():n}@integracao.local";
        string authHash = Convert.ToBase64String(enrollment.AuthHash);
        var deviceId = Guid.NewGuid();

        var body = new RemoteOps.Cloud.Auth.RegisterRequest(
            Email: email,
            Argon2Salt: Convert.ToBase64String(enrollment.Argon2Salt),
            Argon2Params: new RemoteOps.Cloud.Auth.Argon2Params(
                enrollment.Params.MemoryKib,
                enrollment.Params.Iterations,
                enrollment.Params.Parallelism,
                enrollment.Params.OutputBytes),
            AuthHash: authHash,
            WrappedAmkPwd: Convert.ToBase64String(enrollment.WrappedAmkPwd),
            WrappedAmkRec: Convert.ToBase64String(enrollment.WrappedAmkRec),
            AmkKeyVersion: 1,
            DeviceId: deviceId.ToString(),
            DeviceName: "PC do operador (A)",
            WorkspaceName: "Rede do operador");

        using HttpResponseMessage resp = await http.PostAsJsonAsync("/auth/register", body);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            // Sem corpo na mensagem (ADR-013): o que interessa é o status, e o corpo do /auth
            // carrega material da conta.
            throw new InvalidOperationException($"POST /auth/register respondeu {(int)resp.StatusCode}.");
        }

        RemoteOps.Cloud.Auth.RegisterResponse reg =
            await resp.Content.ReadFromJsonAsync<RemoteOps.Cloud.Auth.RegisterResponse>()
            ?? throw new InvalidOperationException("POST /auth/register devolveu corpo vazio.");

        return new CloudAccountFixture(
            email,
            authHash,
            enrollment.Amk,
            reg.WorkspaceId,
            new TokenSet(reg.AccessToken, reg.RefreshToken, reg.ExpiresAt),
            deviceId);
    }

    /// <summary>
    /// O SEGUNDO PC: mesma conta, device novo. Vai pelo <c>POST /auth/login</c> real, com o AuthHash
    /// (nunca a senha) — é assim que o operador entra no outro computador, e é a sessão desse device
    /// que os clientes HTTP do <see cref="CloudSyncedDevice"/> vão usar.
    /// </summary>
    public async Task<(TokenSet Tokens, Guid DeviceId)> LoginNewDeviceAsync(
        CloudApiFactory factory, string deviceName)
    {
        using HttpClient http = factory.CreateClient();
        var deviceId = Guid.NewGuid();

        var body = new RemoteOps.Cloud.Auth.LoginRequest(_email, null, deviceId.ToString(), deviceName)
        {
            AuthHash = _authHashBase64,
        };

        using HttpResponseMessage resp = await http.PostAsJsonAsync("/auth/login", body);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"POST /auth/login respondeu {(int)resp.StatusCode}.");
        }

        RemoteOps.Cloud.Auth.LoginResponse login =
            await resp.Content.ReadFromJsonAsync<RemoteOps.Cloud.Auth.LoginResponse>()
            ?? throw new InvalidOperationException("POST /auth/login devolveu corpo vazio.");

        return (new TokenSet(login.AccessToken, login.RefreshToken, login.ExpiresAt), deviceId);
    }
}
