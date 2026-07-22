using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Security.Account;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Contrato HTTP do time: rota, JSON camelCase e status codes. O teste de serviço não cobre nada
/// disto — e é aqui que aparece se o aceite ficou trancado atrás de uma guarda de membership (o
/// convidado, por definição, ainda não é membro).
/// </summary>
public sealed class TeamApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [Fact]
    public async Task Convite_RoundTripCompleto_SobreHttp()
    {
        using var factory = new CloudApiFactory();
        using var owner = factory.CreateClient();
        using var invitee = factory.CreateClient();

        var dono = await RegisterAsync(owner, "dono@test.local");
        var workspaceId = dono.WorkspaceId;
        Auth(owner, dono.Token, dono.DeviceId);

        // O convidado precisa de conta antes de aceitar (é o fluxo do estágio 1d).
        var colega = await RegisterAsync(invitee, "colega@test.local");
        var inviteeDevice = colega.DeviceId;
        var inviteeRefresh = colega.RefreshToken;
        Auth(invitee, colega.Token, inviteeDevice);

        var code = RecoveryKeyCodec.Generate();
        var create = await owner.PostAsJsonAsync($"/workspaces/{workspaceId}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Manager,
            codeHash = Sha256Hex(code),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var inviteId = created.GetProperty("inviteId").GetString();
        Assert.True(created.GetProperty("emailDelivered").GetBoolean());
        // O corpo da resposta não pode devolver nem o código nem a prova dele.
        Assert.DoesNotContain(code, created.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sha256Hex(code), created.ToString(), StringComparison.OrdinalIgnoreCase);

        var context = await invitee.PostAsJsonAsync(
            $"/invites/{inviteId}/context", new { codeHash = Sha256Hex(code) });
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        var ctxBody = await context.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(workspaceId, ctxBody.GetProperty("workspaceId").GetString());

        var rewrapped = Convert.ToBase64String(Rand(60));
        var accept = await invitee.PostAsJsonAsync(
            $"/invites/{inviteId}/accept", new { codeHash = Sha256Hex(code), wrappedWk = rewrapped });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var accepted = await accept.Content.ReadFromJsonAsync<JsonElement>();

        // A ARMADILHA, documentada em teste: o token que o convidado tem na mão foi emitido quando
        // ele só pertencia ao PRÓPRIO tenant, e o claim tenant_id faz a guarda cross-tenant recusar
        // o workspace do time. Sem o aviso do servidor, o aceite "funcionaria" e o cofre do time
        // responderia 403 até o token expirar — sem ninguém entender por quê.
        Assert.True(accepted.GetProperty("sessionRefreshRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await invitee.GetAsync($"/workspaces/{workspaceId}/key")).StatusCode);

        Auth(invitee, await RefreshAsync(invitee, inviteeRefresh, inviteeDevice), inviteeDevice);

        // O membro novo baixa o PRÓPRIO embrulho — é o que o segundo device dele vai precisar.
        var key = await invitee.GetAsync($"/workspaces/{workspaceId}/key");
        Assert.Equal(HttpStatusCode.OK, key.StatusCode);
        var keyBody = await key.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(rewrapped, keyBody.GetProperty("wrappedWk").GetString());

        var members = await owner.GetAsync($"/workspaces/{workspaceId}/members");
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);
        var list = await members.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, list.GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task CodigoErrado_Devolve400Generico_IgualAoConviteInexistente()
    {
        using var factory = new CloudApiFactory();
        using var owner = factory.CreateClient();
        using var invitee = factory.CreateClient();

        var dono = await RegisterAsync(owner, "dono@test.local");
        var workspaceId = dono.WorkspaceId;
        Auth(owner, dono.Token, dono.DeviceId);
        var colega = await RegisterAsync(invitee, "colega@test.local");
        Auth(invitee, colega.Token, colega.DeviceId);

        var code = RecoveryKeyCodec.Generate();
        var create = await owner.PostAsJsonAsync($"/workspaces/{workspaceId}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Manager,
            codeHash = Sha256Hex(code),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        var inviteId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inviteId").GetString();

        var errado = await invitee.PostAsJsonAsync(
            $"/invites/{inviteId}/context", new { codeHash = Sha256Hex(RecoveryKeyCodec.Generate()) });
        var inexistente = await invitee.PostAsJsonAsync(
            $"/invites/{Guid.NewGuid()}/context", new { codeHash = Sha256Hex(code) });

        // Mesmo status E mesmo detalhe: nada distingue "existe convite, código errado" de "não
        // existe convite nenhum" — senão o endpoint vira oráculo.
        Assert.Equal(HttpStatusCode.BadRequest, errado.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, inexistente.StatusCode);
        Assert.Equal(
            (await errado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(),
            (await inexistente.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Convite_Devolve403_ParaWorkspaceSemMembership()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var forasteiro = await RegisterAsync(client, "forasteiro@test.local");
        Auth(client, forasteiro.Token, forasteiro.DeviceId);

        var resp = await client.PostAsJsonAsync($"/workspaces/{Guid.NewGuid()}/invites", new
        {
            email = "alvo@test.local",
            role = Roles.Manager,
            codeHash = Sha256Hex("qualquer"),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Aceite_Devolve401_SemToken()
    {
        using var factory = new CloudApiFactory();
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            $"/invites/{Guid.NewGuid()}/accept",
            new { codeHash = Sha256Hex("x"), wrappedWk = Convert.ToBase64String(Rand(60)) });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void Auth(HttpClient client, string token, string deviceId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Device-Id");
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
    }

    /// <summary>Renova a sessão: é o que o cliente faz depois de um aceite com sessionRefreshRequired.</summary>
    private static async Task<string> RefreshAsync(HttpClient client, string refreshToken, string deviceId)
    {
        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken, deviceId });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private sealed record Account(string Token, string RefreshToken, string WorkspaceId, string DeviceId);

    private static async Task<Account> RegisterAsync(HttpClient client, string email)
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
            deviceName = "Device",
            workspaceName = "Time do ISP",
        });
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return new Account(
            json.GetProperty("accessToken").GetString()!,
            json.GetProperty("refreshToken").GetString()!,
            json.GetProperty("workspaceId").GetString()!,
            deviceId);
    }
}
