using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using RemoteOps.Cloud.Rbac;
using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// <c>PUT /workspaces/{id}/key</c> — o embrulho da chave do time <b>de quem pergunta</b>, publicado
/// sem depender de convite nenhum.
///
/// <para><b>O buraco que este endpoint fecha:</b> até aqui, o único caminho que gravava
/// <c>Membership.WrappedWk</c> era o ACEITE do convite. Quem <b>cria</b> o time nunca subia o
/// próprio embrulho, então <c>GET /workspaces/{id}/key</c> devolvia 404 <b>para o dono</b>. No
/// segundo computador dele não havia o que restaurar, o chaveiro sorteava uma WK nova e o cofre do
/// time <b>bifurcava</b> — com o indicador ainda afirmando "cofre pessoal". Não era hipótese: era o
/// caminho normal do dono.</para>
///
/// <para><b>A fronteira E2EE continua onde estava:</b> o corpo é um blob OPACO. O servidor não tem
/// a AMK de ninguém, então ele não sabe — e não pode saber — se dois blobs diferentes guardam a
/// MESMA chave (o nonce muda a cada embrulho) ou chaves diferentes. Por isso a detecção possível é
/// por <b>presença e igualdade de bytes</b>, nunca por comparação de chave: quem compara chave é o
/// cliente, que tem a AMK. Qualquer verificação mais esperta aqui exigiria o servidor conhecer a
/// chave, que é exatamente o que o E2EE proíbe.</para>
/// </summary>
public sealed class TeamWorkspaceKeyApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// <b>O achado, em uma frase:</b> o dono publica o próprio embrulho <b>sem convidar ninguém</b> e
    /// o <c>GET</c> passa a devolvê-lo. É isto que faz o SEGUNDO computador dele restaurar em vez de
    /// sortear outra chave.
    /// </summary>
    [Fact]
    public async Task Dono_PublicaOProprioEmbrulho_SemConviteNenhum()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        // Antes: o dono do time recém-criado não tem embrulho no servidor — é a exposição.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.GetAsync($"/workspaces/{dono.WorkspaceId}/key")).StatusCode);

        string wrapped = Convert.ToBase64String(Rand(60));
        var put = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = wrapped, wkVersion = 1 });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("stored").GetBoolean());
        Assert.Equal(1, body.GetProperty("wkVersion").GetInt32());

        var get = await http.GetAsync($"/workspaces/{dono.WorkspaceId}/key");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var keyBody = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrapped, keyBody.GetProperty("wrappedWk").GetString());
    }

    /// <summary>
    /// Idempotência real: republicar o MESMO blob (o app faz isso a cada boot) responde 200 dizendo
    /// que não gravou nada. Sem isto, o reparo de boot viraria um conflito por dia.
    /// </summary>
    [Fact]
    public async Task PublicarOMesmoBlobDeNovo_NaoGravaDeNovo_ENaoEConflito()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string wrapped = Convert.ToBase64String(Rand(60));
        var primeiro = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = wrapped, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, primeiro.StatusCode);
        Assert.True((await primeiro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        var segundo = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = wrapped, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, segundo.StatusCode);
        Assert.False((await segundo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        var get = await http.GetAsync($"/workspaces/{dono.WorkspaceId}/key");
        var keyBody = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(wrapped, keyBody.GetProperty("wrappedWk").GetString());
    }

    /// <summary>
    /// <b>Gravação dupla divergente NÃO troca em silêncio.</b> O servidor não consegue provar que o
    /// segundo blob guarda outra chave (blobs opacos, nonce novo a cada embrulho) — mas ele SABE que
    /// já tem um embrulho para aquele membro, e uma segunda gravação diferente é sinal de
    /// bifurcação. Recusa alta (409) e o blob original fica INTACTO: aceitar por cima faria o
    /// terceiro device restaurar uma chave que não abre o cofre do time.
    /// </summary>
    [Fact]
    public async Task GravacaoDivergente_Recusa409_ENaoTrocaOEmbrulhoGuardado()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string original = Convert.ToBase64String(Rand(60));
        (await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = original, wkVersion = 1 }))
            .EnsureSuccessStatusCode();

        var divergente = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });

        Assert.Equal(HttpStatusCode.Conflict, divergente.StatusCode);

        var get = await http.GetAsync($"/workspaces/{dono.WorkspaceId}/key");
        var keyBody = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(original, keyBody.GetProperty("wrappedWk").GetString());
    }

    /// <summary>
    /// Cada membro só enxerga e só grava o PRÓPRIO embrulho. O convidado publicando o dele não
    /// encosta no do dono — e vice-versa. Sem esta separação, um membro sobrescreveria a chave do
    /// outro e o cofre do colega deixaria de abrir no próximo computador dele.
    /// </summary>
    [Fact]
    public async Task CadaMembroGravaSoOProprioEmbrulho()
    {
        using var factory = new CloudApiFactory();
        using var ownerHttp = factory.CreateClient();
        using var inviteeHttp = factory.CreateClient();

        Account dono = await RegisterAsync(ownerHttp, "dono@test.local");
        Account colega = await RegisterAsync(inviteeHttp, "colega@test.local");
        Auth(ownerHttp, dono.Token, dono.DeviceId);
        Auth(inviteeHttp, colega.Token, colega.DeviceId);

        string doDono = Convert.ToBase64String(Rand(60));
        (await ownerHttp.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = doDono, wkVersion = 1 }))
            .EnsureSuccessStatusCode();

        // O colega entra no time pelo convite (é o único jeito de virar membro).
        string code = RecoveryKeyCodec.Generate();
        var create = await ownerHttp.PostAsJsonAsync($"/workspaces/{dono.WorkspaceId}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Operator,
            codeHash = Sha256Hex(code),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        create.EnsureSuccessStatusCode();
        string inviteId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("inviteId").GetString()!;

        string doColega = Convert.ToBase64String(Rand(60));
        var accept = await inviteeHttp.PostAsJsonAsync(
            $"/invites/{inviteId}/accept", new { codeHash = Sha256Hex(code), wrappedWk = doColega });
        accept.EnsureSuccessStatusCode();

        // Entrar no time do dono faz a conta pertencer a dois tenants: o token na mão do colega
        // ficou obsoleto e o servidor avisou. Sem renovar, tudo do time volta 403.
        Auth(inviteeHttp, await RefreshAsync(inviteeHttp, colega.RefreshToken, colega.DeviceId), colega.DeviceId);

        // Republicar o próprio blob é no-op para ele...
        var doColegaDeNovo = await inviteeHttp.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = doColega, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, doColegaDeNovo.StatusCode);
        Assert.False((await doColegaDeNovo.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stored").GetBoolean());

        // ...e não encostou no embrulho do DONO, que continua o dele.
        var getDono = await ownerHttp.GetAsync($"/workspaces/{dono.WorkspaceId}/key");
        Assert.Equal(doDono, (await getDono.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("wrappedWk").GetString());

        var getColega = await inviteeHttp.GetAsync($"/workspaces/{dono.WorkspaceId}/key");
        Assert.Equal(doColega, (await getColega.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("wrappedWk").GetString());
    }

    /// <summary>
    /// Quem não é do time não publica nada — 403, como o GET e como todo o resto do workspace. Um
    /// estranho conseguindo gravar aqui plantaria a chave dele na membership alheia.
    /// </summary>
    [Fact]
    public async Task NaoMembro_NaoPublica()
    {
        using var factory = new CloudApiFactory();
        using var donoHttp = factory.CreateClient();
        using var estranhoHttp = factory.CreateClient();

        Account dono = await RegisterAsync(donoHttp, "dono@test.local");
        Account estranho = await RegisterAsync(estranhoHttp, "estranho@test.local");
        Auth(estranhoHttp, estranho.Token, estranho.DeviceId);

        var put = await estranhoHttp.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        // E nada foi plantado: o dono continua sem embrulho (ele ainda não publicou o dele).
        Auth(donoHttp, dono.Token, dono.DeviceId);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await donoHttp.GetAsync($"/workspaces/{dono.WorkspaceId}/key")).StatusCode);
    }

    /// <summary>
    /// Blob torto morre na porta (400). O piso/teto é o mesmo do convite: um embrulho truncado tem
    /// de falhar AQUI, e não meses depois, na máquina do colega, como "o cofre não abre".
    /// </summary>
    [Theory]
    [InlineData("nao-e-base64!!")]
    [InlineData("YmxvYg==")] // 4 bytes: pequeno demais para nonce+tag+chave
    public async Task BlobTorto_Recusa400(string wrapped)
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        var put = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key", new { wrappedWk = wrapped, wkVersion = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    /// <summary>
    /// <c>wkVersion</c> abaixo de 1 é recusado, como no convite: versão zero seria "membership sem
    /// chave", e gravar um embrulho carimbado assim tornaria uma rotação futura indetectável.
    /// </summary>
    [Fact]
    public async Task VersaoInvalida_Recusa400()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        var put = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ── O cliente REAL contra o backend REAL ──────────────────────────────────
    //
    // Os três desfechos vêm no STATUS (200 com `stored: true`, 200 com `stored: false`, 409), não no
    // corpo. Um fake devolvendo o enum pronto testaria a minha suposição sobre o servidor, não o
    // servidor — e errar aqui custa direto: um 409 lido como "publicado" faria o app achar que a
    // chave subiu quando quem está guardada é outra.

    [Fact]
    public async Task Cliente_Publica_DepoisRepublica_EPorFimConflita()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");

        TeamApiClient api = ClientFor(http, dono);
        string wrapped = Convert.ToBase64String(Rand(60));

        Assert.Equal(
            TeamKeyPublication.Stored,
            await api.PublishWorkspaceKeyAsync(
                dono.WorkspaceId, new PublishTeamWorkspaceKeyRequest(wrapped, 1)));

        Assert.Equal(
            TeamKeyPublication.AlreadyPublished,
            await api.PublishWorkspaceKeyAsync(
                dono.WorkspaceId, new PublishTeamWorkspaceKeyRequest(wrapped, 1)));

        Assert.Equal(
            TeamKeyPublication.Divergent,
            await api.PublishWorkspaceKeyAsync(
                dono.WorkspaceId,
                new PublishTeamWorkspaceKeyRequest(Convert.ToBase64String(Rand(60)), 1)));

        // E o cliente lê de volta EXATAMENTE o que publicou primeiro.
        TeamWorkspaceKeyResponse? guardada = await api.GetWorkspaceKeyAsync(dono.WorkspaceId);
        Assert.Equal(wrapped, guardada!.WrappedWk);
    }

    /// <summary>
    /// Status que NÃO é 200 nem 409 continua estourando. Um 403 (membership cortada) virando
    /// "publicado" deixaria o dono achando que a chave subiu quando ela nunca saiu do PC dele.
    /// </summary>
    [Fact]
    public async Task Cliente_ComAcessoNegado_ESTOURA_EmVezDeFingirQuePublicou()
    {
        using var factory = new CloudApiFactory();
        using var donoHttp = factory.CreateClient();
        using var estranhoHttp = factory.CreateClient();

        Account dono = await RegisterAsync(donoHttp, "dono@test.local");
        Account estranho = await RegisterAsync(estranhoHttp, "estranho@test.local");

        TeamApiClient api = ClientFor(estranhoHttp, estranho);

        var ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => api.PublishWorkspaceKeyAsync(
                dono.WorkspaceId,
                new PublishTeamWorkspaceKeyRequest(Convert.ToBase64String(Rand(60)), 1)));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    private static TeamApiClient ClientFor(HttpClient http, Account account) =>
        new(http, Guid.Parse(account.DeviceId),
            new FakeTokenStore(new TokenSet(account.Token, account.RefreshToken, DateTimeOffset.UtcNow.AddHours(1))));

    // ── Helpers (espelhos dos de TeamApiTests) ────────────────────────────────

    private static void Auth(HttpClient client, string token, string deviceId)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Remove("X-Device-Id");
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId);
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

    private static async Task<string> RefreshAsync(HttpClient client, string refreshToken, string deviceId)
    {
        var resp = await client.PostAsJsonAsync("/auth/refresh", new { refreshToken, deviceId });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }
}
