using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
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
///
/// <para><b>Todo teste daqui usa um workspace de TIME</b>, e não o cofre pessoal do dono. Não é
/// arrumação: desde a marca de nascimento, publicar chave de time num workspace pessoal é recusado
/// com 422 — o blob guardado ali é o que faz o aplicativo tratar aquele cofre como compartilhado, e
/// plantá-lo no cofre pessoal do operador colocaria os ~700 clientes dele sob o regime do time. Essa
/// recusa tem casa própria (<c>PersonalWorkspaceGuardTests</c>).</para>
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
    ///
    /// <para>O ponto de partida — time SEM embrulho guardado — é o que sobrou dos times criados por
    /// versões anteriores do cliente; hoje o <c>POST /workspaces</c> grava workspace e embrulho na
    /// MESMA transação, então ele não é mais alcançável por endpoint nenhum e precisa ser encenado.
    /// Encená-lo é justamente o ponto: é a máquina que este endpoint existe para consertar.</para>
    /// </summary>
    [Fact]
    public async Task Dono_PublicaOProprioEmbrulho_SemConviteNenhum()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string time = await CreateTeamAsync(http, "Clientes do ISP");
        await ClearStoredWrapAsync(factory, time);

        // Antes: o dono do time não tem embrulho no servidor — é a exposição.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/workspaces/{time}/key")).StatusCode);

        string wrapped = Convert.ToBase64String(Rand(60));
        var put = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key", new { wrappedWk = wrapped, wkVersion = 1 });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("stored").GetBoolean());
        Assert.Equal(1, body.GetProperty("wkVersion").GetInt32());

        var get = await http.GetAsync($"/workspaces/{time}/key");
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

        string time = await CreateTeamAsync(http, "Clientes do ISP");
        await ClearStoredWrapAsync(factory, time);

        string wrapped = Convert.ToBase64String(Rand(60));
        var primeiro = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key", new { wrappedWk = wrapped, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, primeiro.StatusCode);
        Assert.True((await primeiro.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        var segundo = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key", new { wrappedWk = wrapped, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, segundo.StatusCode);
        Assert.False((await segundo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());

        var get = await http.GetAsync($"/workspaces/{time}/key");
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

        // O embrulho original entra JUNTO com o time — é assim que ele nasce hoje.
        string original = Convert.ToBase64String(Rand(60));
        string time = await CreateTeamAsync(http, "Clientes do ISP", original);

        var divergente = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });

        Assert.Equal(HttpStatusCode.Conflict, divergente.StatusCode);

        var get = await http.GetAsync($"/workspaces/{time}/key");
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
        string time = await CreateTeamAsync(ownerHttp, "Clientes do ISP", doDono);

        // O colega entra no time pelo convite (é o único jeito de virar membro).
        string code = RecoveryKeyCodec.Generate();
        var create = await ownerHttp.PostAsJsonAsync($"/workspaces/{time}/invites", new
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
            $"/workspaces/{time}/key", new { wrappedWk = doColega, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, doColegaDeNovo.StatusCode);
        Assert.False((await doColegaDeNovo.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("stored").GetBoolean());

        // ...e não encostou no embrulho do DONO, que continua o dele.
        var getDono = await ownerHttp.GetAsync($"/workspaces/{time}/key");
        Assert.Equal(doDono, (await getDono.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("wrappedWk").GetString());

        var getColega = await inviteeHttp.GetAsync($"/workspaces/{time}/key");
        Assert.Equal(doColega, (await getColega.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("wrappedWk").GetString());
    }

    /// <summary>
    /// Quem não é do time não publica nada — 403, como o GET e como todo o resto do workspace. Um
    /// estranho conseguindo gravar aqui plantaria a chave dele na membership alheia.
    ///
    /// <para><b>403, e não 422:</b> o RBAC roda ANTES da marca de nascimento, de propósito. Na ordem
    /// inversa, a recusa por "cofre pessoal" viraria um oráculo — qualquer conta autenticada
    /// descobriria, um GUID por vez, se um workspace alheio existe e de que tipo ele é.</para>
    /// </summary>
    [Fact]
    public async Task NaoMembro_NaoPublica()
    {
        using var factory = new CloudApiFactory();
        using var donoHttp = factory.CreateClient();
        using var estranhoHttp = factory.CreateClient();

        Account dono = await RegisterAsync(donoHttp, "dono@test.local");
        Account estranho = await RegisterAsync(estranhoHttp, "estranho@test.local");
        Auth(donoHttp, dono.Token, dono.DeviceId);
        Auth(estranhoHttp, estranho.Token, estranho.DeviceId);

        string doDono = Convert.ToBase64String(Rand(60));
        string time = await CreateTeamAsync(donoHttp, "Clientes do ISP", doDono);

        var put = await estranhoHttp.PutAsJsonAsync(
            $"/workspaces/{time}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        // E nada foi plantado: o embrulho do dono continua o dele, byte a byte.
        var get = await donoHttp.GetAsync($"/workspaces/{time}/key");
        Assert.Equal(doDono, (await get.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("wrappedWk").GetString());
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
        string time = await CreateTeamAsync(http, "Clientes do ISP");

        var put = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key", new { wrappedWk = wrapped, wkVersion = 1 });

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
        string time = await CreateTeamAsync(http, "Clientes do ISP");

        var put = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key",
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
        Auth(http, dono.Token, dono.DeviceId);

        string time = await CreateTeamAsync(http, "Clientes do ISP");
        await ClearStoredWrapAsync(factory, time);

        TeamApiClient api = ClientFor(http, dono);
        string wrapped = Convert.ToBase64String(Rand(60));

        Assert.Equal(
            TeamKeyPublication.Stored,
            await api.PublishWorkspaceKeyAsync(time, new PublishTeamWorkspaceKeyRequest(wrapped, 1)));

        Assert.Equal(
            TeamKeyPublication.AlreadyPublished,
            await api.PublishWorkspaceKeyAsync(time, new PublishTeamWorkspaceKeyRequest(wrapped, 1)));

        Assert.Equal(
            TeamKeyPublication.Divergent,
            await api.PublishWorkspaceKeyAsync(
                time, new PublishTeamWorkspaceKeyRequest(Convert.ToBase64String(Rand(60)), 1)));

        // E o cliente lê de volta EXATAMENTE o que publicou primeiro.
        TeamWorkspaceKeyResponse? guardada = await api.GetWorkspaceKeyAsync(time);
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
        Auth(donoHttp, dono.Token, dono.DeviceId);

        string time = await CreateTeamAsync(donoHttp, "Clientes do ISP");
        TeamApiClient api = ClientFor(estranhoHttp, estranho);

        var ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => api.PublishWorkspaceKeyAsync(
                time, new PublishTeamWorkspaceKeyRequest(Convert.ToBase64String(Rand(60)), 1)));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    private static TeamApiClient ClientFor(HttpClient http, Account account) =>
        new(http, Guid.Parse(account.DeviceId),
            new FakeTokenStore(new TokenSet(account.Token, account.RefreshToken, DateTimeOffset.UtcNow.AddHours(1))));

    // ── Helpers (espelhos dos de TeamApiTests) ────────────────────────────────

    /// <summary>Cria um workspace de TIME pelo endpoint real e devolve o id sorteado no cliente.</summary>
    private static async Task<string> CreateTeamAsync(HttpClient client, string name, string? wrappedWk = null)
    {
        var id = Guid.NewGuid().ToString();
        var resp = await client.PostAsJsonAsync("/workspaces", new
        {
            id,
            name,
            wrappedWk = wrappedWk ?? Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        resp.EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>
    /// Apaga o embrulho guardado das memberships do workspace, encenando o time criado por uma
    /// versão do cliente ANTERIOR ao <c>POST /workspaces</c> — quando workspace e embrulho não
    /// nasciam juntos. É o estado que o <c>PUT /key</c> existe para consertar e que, justamente por
    /// isso, nenhum endpoint de hoje consegue produzir.
    /// </summary>
    private static Task ClearStoredWrapAsync(CloudApiFactory factory, string workspaceId) =>
        factory.WithDbAsync(async db =>
        {
            var id = Guid.Parse(workspaceId);
            var memberships = await db.Memberships.Where(m => m.WorkspaceId == id).ToListAsync();
            foreach (var m in memberships)
            {
                m.WrappedWk = null;
                m.WkVersion = 0;
            }

            await db.SaveChangesAsync();
        });

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
            workspaceName = "Cofre pessoal",
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
