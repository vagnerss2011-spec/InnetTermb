using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Contrato HTTP de verdade: rota, JSON camelCase, status codes e rate limit.
/// É o que o RemoteOps.Desktop vai chamar — o teste de serviço não cobre isso.
/// </summary>
public sealed class CloudApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static object NewRegisterBody(string email, string authHashB64, string saltB64, string wrappedPwdB64) => new
    {
        email,
        argon2Salt = saltB64,
        argon2Params = new { memoryKib = 65536, iterations = 3, parallelism = 1, outputBytes = 32 },
        authHash = authHashB64,
        wrappedAmkPwd = wrappedPwdB64,
        wrappedAmkRec = Convert.ToBase64String(Rand(60)),
        amkKeyVersion = 1,
        deviceId = Guid.NewGuid().ToString(),
        deviceName = "Device A",
        workspaceName = "Meu Workspace",
    };

    // ── Round-trip completo sobre HTTP ────────────────────────────────────────

    [Fact]
    public async Task Register_Kdf_Login_RoundTrip_OverHttp()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var authHash = Convert.ToBase64String(Rand(32));
        var salt = Convert.ToBase64String(Rand(16));
        var wrappedPwd = Convert.ToBase64String(Rand(60));
        const string email = "operador@test.local";

        var regResp = await client.PostAsJsonAsync("/auth/register", NewRegisterBody(email, authHash, salt, wrappedPwd));
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

        var reg = await regResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(reg.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(reg.GetProperty("workspaceId").GetString()));
        Assert.Equal(wrappedPwd, reg.GetProperty("wrappedAmkPwd").GetString());

        var kdfResp = await client.GetAsync($"/auth/kdf?email={Uri.EscapeDataString(email)}");
        Assert.Equal(HttpStatusCode.OK, kdfResp.StatusCode);
        var kdf = await kdfResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(salt, kdf.GetProperty("argon2Salt").GetString());
        Assert.Equal(65536, kdf.GetProperty("argon2Params").GetProperty("memoryKib").GetInt32());

        var loginResp = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            authHash,
            deviceId = Guid.NewGuid().ToString(),
            deviceName = "Device B",
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var login = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrappedPwd, login.GetProperty("wrappedAmkPwd").GetString());
        Assert.Equal(1, login.GetProperty("amkKeyVersion").GetInt32());
        Assert.Equal(1, login.GetProperty("workspaces").GetArrayLength());
    }

    [Fact]
    public async Task Login_Returns401_WhenAuthHashWrong()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();
        const string email = "operador@test.local";

        await client.PostAsJsonAsync("/auth/register",
            NewRegisterBody(email, Convert.ToBase64String(Rand(32)),
                Convert.ToBase64String(Rand(16)), Convert.ToBase64String(Rand(60))));

        var resp = await client.PostAsJsonAsync("/auth/login", new
        {
            email,
            authHash = Convert.ToBase64String(Rand(32)),
            deviceId = Guid.NewGuid().ToString(),
            deviceName = "Device B",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Register_Returns409_ForDuplicateEmail()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();
        const string email = "operador@test.local";

        var body = NewRegisterBody(email, Convert.ToBase64String(Rand(32)),
            Convert.ToBase64String(Rand(16)), Convert.ToBase64String(Rand(60)));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/register", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/auth/register", body)).StatusCode);
    }

    [Fact]
    public async Task Kdf_ReturnsSameShape_ForUnknownEmail_OverHttp()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/auth/kdf?email=naoexiste@test.local");

        // Indistinguível de conta real: 200 com o mesmo shape.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var kdf = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(16, Convert.FromBase64String(kdf.GetProperty("argon2Salt").GetString()!).Length);
        Assert.Equal(65536, kdf.GetProperty("argon2Params").GetProperty("memoryKib").GetInt32());
    }

    [Fact]
    public async Task Register_Returns400_ForWeakArgon2Params()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/register", new
        {
            email = "fraco@test.local",
            argon2Salt = Convert.ToBase64String(Rand(16)),
            argon2Params = new { memoryKib = 8, iterations = 1, parallelism = 1, outputBytes = 32 },
            authHash = Convert.ToBase64String(Rand(32)),
            wrappedAmkPwd = Convert.ToBase64String(Rand(60)),
            wrappedAmkRec = Convert.ToBase64String(Rand(60)),
            amkKeyVersion = 1,
            deviceId = Guid.NewGuid().ToString(),
            deviceName = "D",
            workspaceName = "WS",
        });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── /secrets sobre HTTP ───────────────────────────────────────────────────

    [Fact]
    public async Task Secrets_Upsert_Then_Pull_OverHttp()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var (token, workspaceId, deviceId) = await RegisterAndAuthAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);

        var envelopeId = Guid.NewGuid().ToString();
        var ciphertext = Convert.ToBase64String(Rand(120));

        var upsert = await client.PostAsJsonAsync("/secrets", new
        {
            workspaceId,
            envelope = new
            {
                id = envelopeId,
                workspaceId,
                ciphertext,
                nonce = Convert.ToBase64String(Rand(12)),
                tag = Convert.ToBase64String(Rand(16)),
                wrappedCek = Convert.ToBase64String(Rand(60)),
                cekNonce = Convert.ToBase64String(Rand(12)),
                cekTag = Convert.ToBase64String(Rand(16)),
                keyVersion = "wdk-v1",
                version = 1,
            },
        });
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);

        var pull = await client.GetAsync($"/secrets?workspaceId={workspaceId}&since=0");
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);

        var body = await pull.Content.ReadFromJsonAsync<JsonElement>();
        var envelopes = body.GetProperty("envelopes");
        Assert.Equal(1, envelopes.GetArrayLength());
        // Blob volta byte a byte — o servidor não interpretou nada.
        Assert.Equal(ciphertext, envelopes[0].GetProperty("ciphertext").GetString());
        Assert.Equal(envelopeId, envelopes[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Secrets_Returns401_WithoutToken()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync($"/secrets?workspaceId={Guid.NewGuid()}&since=0");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Secrets_Returns403_ForWorkspaceWithoutMembership()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var (token, _, deviceId) = await RegisterAndAuthAsync(client, "dono@test.local");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);

        // Workspace de outra conta (na prática: qualquer workspace sem membership).
        var resp = await client.GetAsync($"/secrets?workspaceId={Guid.NewGuid()}&since=0");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── Rate limit ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auth_RateLimits_AfterBurst()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        // 20 permissões por minuto por IP; no TestServer todas caem na mesma partição.
        var sawTooMany = false;
        for (var i = 0; i < 40 && !sawTooMany; i++)
        {
            var resp = await client.GetAsync($"/auth/kdf?email=alvo{i}@test.local");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) sawTooMany = true;
        }

        Assert.True(sawTooMany, "O /auth deveria devolver 429 depois do burst — rate limiter não está aplicado.");
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static async Task<(string Token, string WorkspaceId, string DeviceId)> RegisterAndAuthAsync(
        HttpClient client, string email = "operador@test.local")
    {
        var deviceId = Guid.NewGuid().ToString();
        var resp = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            argon2Salt = Convert.ToBase64String(Rand(16)),
            argon2Params = new { memoryKib = 65536, iterations = 3, parallelism = 1, outputBytes = 32 },
            authHash = Convert.ToBase64String(Rand(32)),
            wrappedAmkPwd = Convert.ToBase64String(Rand(60)),
            wrappedAmkRec = Convert.ToBase64String(Rand(60)),
            amkKeyVersion = 1,
            deviceId,
            deviceName = "Device A",
            workspaceName = "Meu Workspace",
        });
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("accessToken").GetString()!,
                json.GetProperty("workspaceId").GetString()!,
                deviceId);
    }
}
