using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Contrato HTTP da recuperação de senha (Fase 4): rotas anônimas, binding camelCase, anti-enumeração
/// (/forgot sempre 202) e o round-trip forgot → reset-context → reset → login novo, sobre HTTP.
/// </summary>
public sealed class PasswordRecoveryApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static object RegisterBody(string email, string authHashB64, string wrappedRecB64) => new
    {
        email,
        argon2Salt = Convert.ToBase64String(Rand(16)),
        argon2Params = new { memoryKib = 65536, iterations = 3, parallelism = 1, outputBytes = 32 },
        authHash = authHashB64,
        wrappedAmkPwd = Convert.ToBase64String(Rand(60)),
        wrappedAmkRec = wrappedRecB64,
        amkKeyVersion = 1,
        deviceId = Guid.NewGuid().ToString(),
        deviceName = "Device A",
        workspaceName = "WS",
    };

    private static string ExtractToken(string body) =>
        body.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.Length >= 40 && l.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));

    private static object ResetBody(string token, string authHashB64) => new
    {
        token,
        newAuthHash = authHashB64,
        newArgon2Salt = Convert.ToBase64String(Rand(16)),
        newArgon2Params = new { memoryKib = 65536, iterations = 3, parallelism = 1, outputBytes = 32 },
        newWrappedAmkPwd = Convert.ToBase64String(Rand(60)),
    };

    [Fact]
    public async Task Forgot_Returns202_ForBothKnownAndUnknownEmail()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();
        const string email = "operador@test.local";

        await client.PostAsJsonAsync("/auth/register",
            RegisterBody(email, Convert.ToBase64String(Rand(32)), Convert.ToBase64String(Rand(60))));

        // Anti-enumeração: conta existente e inexistente são indistinguíveis (ambas 202).
        var known = await client.PostAsJsonAsync("/auth/password/forgot", new { email });
        var unknown = await client.PostAsJsonAsync("/auth/password/forgot", new { email = "naoexiste@test.local" });

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
    }

    [Fact]
    public async Task ResetContext_And_Reset_Return400_ForUnknownToken()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var ctx = await client.PostAsJsonAsync("/auth/password/reset-context", new { token = "nao-existe" });
        var reset = await client.PostAsJsonAsync("/auth/password/reset",
            ResetBody("nao-existe", Convert.ToBase64String(Rand(32))));

        Assert.Equal(HttpStatusCode.BadRequest, ctx.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    [Fact]
    public async Task Forgot_ResetContext_Reset_Login_RoundTrip_OverHttp()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();
        const string email = "operador@test.local";
        var wrappedRec = Convert.ToBase64String(Rand(60));

        await client.PostAsJsonAsync("/auth/register",
            RegisterBody(email, Convert.ToBase64String(Rand(32)), wrappedRec));

        // 1) Esqueci a senha → 202, e o "email" (fake) recebe o token.
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/auth/password/forgot", new { email })).StatusCode);
        var token = ExtractToken(factory.Email.Last!.TextBody);

        // 2) reset-context devolve o wrapped_amk_rec (igual ao registrado).
        var ctxResp = await client.PostAsJsonAsync("/auth/password/reset-context", new { token });
        Assert.Equal(HttpStatusCode.OK, ctxResp.StatusCode);
        var ctxBody = await ctxResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrappedRec, ctxBody.GetProperty("wrappedAmkRec").GetString());

        // 3) reset com material novo (opaco aqui) → 204.
        var newAuthHash = Convert.ToBase64String(Rand(32));
        var resetResp = await client.PostAsJsonAsync("/auth/password/reset", ResetBody(token, newAuthHash));
        Assert.Equal(HttpStatusCode.NoContent, resetResp.StatusCode);

        // 4) A senha NOVA loga; o token é de uso único (segundo reset falha).
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            authHash = newAuthHash,
            deviceId = Guid.NewGuid().ToString(),
            deviceName = "Device B",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var replay = await client.PostAsJsonAsync("/auth/password/reset", ResetBody(token, Convert.ToBase64String(Rand(32))));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }
}
