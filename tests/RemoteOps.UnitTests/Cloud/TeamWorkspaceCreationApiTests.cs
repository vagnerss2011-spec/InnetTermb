using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// <c>POST /workspaces</c> — o time nasce como workspace PRÓPRIO, e não como o workspace pessoal do
/// dono virando "de time".
///
/// <para><b>O buraco que este endpoint fecha:</b> até aqui não existia nenhum caminho para CRIAR
/// workspace fora do <c>/auth/register</c>. Consequência: o convite só podia ser emitido contra o
/// workspace ATIVO, que é o pessoal do dono — e o <c>/sync</c> é escopado por workspace + membership.
/// Convidar o colega baixaria os ~700 clientes do operador (nomes, IPs, grupos, vendors) inteiros no
/// PC dele. Isso contradiz frontalmente a decisão do operador ("o time começa vazio") e não é
/// consertável no cliente: o vazamento é de METADADO, e o indicador de cofre só fala de senha.</para>
///
/// <para><b>Por que o id vem do CLIENTE:</b> o AAD do embrulho da WK é <c>"wk|time:{id}"</c>, então a
/// chave não existe antes do id. Deixar o servidor gerar exigiria duas idas — e entre elas existiria
/// um workspace de time SEM chave, que é exatamente o estado em que o app não consegue dizer se está
/// no cofre pessoal ou no do time. Id duplicado é recusado com 409, sem revelar de quem é.</para>
/// </summary>
public sealed class TeamWorkspaceCreationApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>
    /// O caminho feliz completo, e é ele que vale a fatia: o workspace de time nasce, a membership
    /// Owner nasce JUNTO com o embrulho da WK, e o <c>GET /key</c> responde 200 no instante
    /// seguinte. Atômico de propósito — a lição do 1e′ é que a custódia do embrulho não pode
    /// depender de um segundo passo que pode falhar.
    /// </summary>
    [Fact]
    public async Task PostWorkspaces_CriaTimeVazio_ComEmbrulhoDoDono()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string id = Guid.NewGuid().ToString();
        string wrapped = Convert.ToBase64String(Rand(60));

        var resp = await http.PostAsJsonAsync(
            "/workspaces", new { id, name = "Clientes do ISP", wrappedWk = wrapped, wkVersion = 1 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetString());
        Assert.Equal("Clientes do ISP", body.GetProperty("name").GetString());
        Assert.Equal("Owner", body.GetProperty("role").GetString());

        // O embrulho subiu na MESMA transação: o dono já restaura a chave no segundo computador.
        var key = await http.GetAsync($"/workspaces/{id}/key");
        Assert.Equal(HttpStatusCode.OK, key.StatusCode);
        var keyBody = await key.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrapped, keyBody.GetProperty("wrappedWk").GetString());
        Assert.Equal(1, keyBody.GetProperty("wkVersion").GetInt32());
    }

    /// <summary>
    /// Id vindo do cliente NÃO pode sequestrar workspace alheio. 409 e nada muda no workspace que já
    /// existe — nem o nome, nem o embrulho do dono dele.
    /// </summary>
    [Fact]
    public async Task PostWorkspaces_IdDuplicado_Recusa409_SemAlterarNada()
    {
        using var factory = new CloudApiFactory();
        using var donoHttp = factory.CreateClient();
        using var estranhoHttp = factory.CreateClient();

        Account dono = await RegisterAsync(donoHttp, "dono@test.local");
        Account estranho = await RegisterAsync(estranhoHttp, "estranho@test.local");
        Auth(estranhoHttp, estranho.Token, estranho.DeviceId);

        // O workspace pessoal do dono é o alvo do sequestro.
        var resp = await estranhoHttp.PostAsJsonAsync("/workspaces", new
        {
            id = dono.WorkspaceId,
            name = "Sequestro",
            wrappedWk = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // E o estranho continua de fora: sem membership, o GET da chave é 403 (não 404, que seria
        // "sou membro e não tenho chave").
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await estranhoHttp.GetAsync($"/workspaces/{dono.WorkspaceId}/key")).StatusCode);
    }

    /// <summary>
    /// <c>WrappedKeyBlob</c> é a definição ÚNICA de "chave embrulhada" — o mesmo piso/teto do convite,
    /// do aceite e da publicação. Blob torto morre na porta, e não meses depois na máquina do colega.
    /// </summary>
    [Theory]
    [InlineData("nao-e-base64!!")]
    [InlineData("YmxvYg==")] // 4 bytes: pequeno demais para nonce+tag+chave
    public async Task PostWorkspaces_BlobInvalido_Recusa400(string wrapped)
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        var resp = await http.PostAsJsonAsync(
            "/workspaces",
            new { id = Guid.NewGuid().ToString(), name = "Time", wrappedWk = wrapped, wkVersion = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// <b>"O time começa vazio" deixa de ser prosa.</b> O <c>/sync/pull</c> do workspace recém-criado
    /// não devolve mudança nenhuma — os ~700 clientes do operador ficam onde estão, no workspace
    /// pessoal, e nunca aparecem no PC de quem for convidado.
    /// </summary>
    [Fact]
    public async Task TimeNasceVazio_NaoTemAssetAlgum()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string id = Guid.NewGuid().ToString();
        var criado = await http.PostAsJsonAsync("/workspaces", new
        {
            id,
            name = "Clientes do ISP",
            wrappedWk = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        Assert.Equal(HttpStatusCode.OK, criado.StatusCode);

        var pull = await http.GetAsync($"/sync/pull?workspaceId={id}&cursor=0&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
        var body = await pull.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("changes").EnumerateArray());
    }

    /// <summary>
    /// ⚠️ <b>O <c>kind</c> VIAJA na lista de workspaces do login</b> — o fato autoritativo saindo do
    /// servidor para o cliente, que é o que substitui o palpite por AUSÊNCIA de chave.
    ///
    /// <para><b>O que existia antes:</b> a coluna <c>workspaces.kind</c> já era gravada (G1) e o
    /// cliente <b>não recebia</b>. Sem ela, o app classificava o workspace ativo pelo 404 de
    /// <c>GET /workspaces/{id}/key</c> — que significa "a SUA CONTA não guarda embrulho aqui" e é
    /// indistinguível de um 404 de infraestrutura. Lido como "não é time", ele fazia o banco com os
    /// ~700 equipamentos do operador virar "o banco pessoal do TIME".</para>
    /// </summary>
    [Fact]
    public async Task Login_DEVOLVE_OKindDeCadaWorkspace_PessoalETime()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string time = Guid.NewGuid().ToString();
        var criado = await http.PostAsJsonAsync("/workspaces", new
        {
            id = time,
            name = "Clientes do ISP",
            wrappedWk = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        Assert.Equal(HttpStatusCode.OK, criado.StatusCode);

        // O login é o caminho REAL: é dele que a lista sai para o boot do cliente.
        var login = await http.PostAsJsonAsync("/auth/login", new
        {
            email = "dono@test.local",
            deviceId = dono.DeviceId,
            deviceName = "Device",
            authHash = dono.AuthHash,
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var workspaces = body.GetProperty("workspaces").EnumerateArray().ToList();
        Assert.Equal(2, workspaces.Count);

        JsonElement doTime = workspaces.Single(w => w.GetProperty("id").GetString() == time);
        JsonElement pessoal = workspaces.Single(w => w.GetProperty("id").GetString() == dono.WorkspaceId);

        Assert.Equal("team", doTime.GetProperty("kind").GetString());
        Assert.Equal("personal", pessoal.GetProperty("kind").GetString());
    }

    // ── Helpers (espelhos dos de TeamWorkspaceKeyApiTests) ────────────────────

    private static void Auth(HttpClient client, string token, string deviceId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Device-Id");
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
    }

    private sealed record Account(
        string Token, string RefreshToken, string WorkspaceId, string DeviceId, string AuthHash);

    private static async Task<Account> RegisterAsync(HttpClient client, string email)
    {
        var deviceId = Guid.NewGuid().ToString();
        var authHash = Convert.ToBase64String(Rand(32));
        var resp = await client.PostAsJsonAsync("/auth/register", new
        {
            email,
            argon2Salt = Convert.ToBase64String(Rand(16)),
            argon2Params = new { memoryKib = 65536, iterations = 3, parallelism = 1, outputBytes = 32 },
            authHash,
            wrappedAmkPwd = Convert.ToBase64String(Rand(60)),
            wrappedAmkRec = Convert.ToBase64String(Rand(60)),
            amkKeyVersion = 1,
            deviceId,
            deviceName = "Device",
            workspaceName = "Cofre pessoal",
        });
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return new Account(
            json.GetProperty("accessToken").GetString()!,
            json.GetProperty("refreshToken").GetString()!,
            json.GetProperty("workspaceId").GetString()!,
            deviceId,
            authHash);
    }
}
