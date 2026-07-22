using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Teams;
using RemoteOps.Security.Account;

using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// <b>O servidor recusa sozinho.</b> Até aqui, <c>WorkspaceEntity</c> não distinguia o cofre PESSOAL
/// de um TIME: convite e <c>PUT /key</c> eram aceitos em QUALQUER workspace onde o chamador tivesse a
/// permissão. Um cliente com bug — ou adulterado — bastava para reabrir o vazamento, e o pior deles
/// não é de senha: convidar alguém para o workspace pessoal do operador entrega os ~700 clientes
/// dele (nomes, endereços, grupos, fabricantes) na máquina do convidado, porque o <c>/sync</c> é
/// escopado por workspace + membership.
///
/// <para><b>Consertar a interface não basta.</b> A tela é uma camada de conveniência; a guarda tem
/// de morar onde nenhum cliente alcança. É por isso que estes testes falam HTTP contra o backend
/// real: é exatamente o que um cliente adulterado enviaria.</para>
///
/// <para><b>Por que 422, e não 403/409:</b> o pedido está bem formado e quem pede TEM a permissão —
/// o que não existe é o objeto da operação (um time). 403 diria "você não pode", que é falso e
/// mandaria o operador procurar permissão; 409 já significa outra coisa no <c>PUT /key</c> (embrulho
/// divergente), e o cliente reage a ele baixando a chave guardada para reconciliar — um caminho que
/// aqui só produziria um segundo recado errado. 422 é status novo nesta API, então nenhum desfecho
/// antigo é reinterpretado, e o motivo legível vai em <c>detail</c> + <c>reason</c>.</para>
/// </summary>
public sealed class PersonalWorkspaceGuardTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>O motivo em forma de máquina: é por ele que a tela do cliente escolhe o que dizer.</summary>
    private const string PersonalReason = "workspace.personal";

    // ── Convite ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>O bloqueante, em um teste.</b> O dono manda um convite para o PRÓPRIO workspace pessoal —
    /// que é o que o botão de convite fazia antes do conserto da interface — e o servidor recusa.
    /// Nem convite gravado, nem e-mail: o colega nunca recebe um link que baixaria o acervo inteiro.
    /// </summary>
    [Fact]
    public async Task ConvidarParaCofrePessoal_RECUSA_ComMotivoProprio()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        var resp = await http.PostAsJsonAsync($"/workspaces/{dono.WorkspaceId}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Operator,
            codeHash = Sha256Hex(RecoveryKeyCodec.Generate()),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PersonalReason, body.GetProperty("reason").GetString());

        // A frase vai PARA A TELA do operador: precisa dizer o que é este workspace e o que fazer.
        string detail = body.GetProperty("detail").GetString()!;
        Assert.Contains("pessoal", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time", detail, StringComparison.OrdinalIgnoreCase);

        // Recusa é recusa: nenhum e-mail com link saiu para o convidado.
        Assert.Empty(factory.Email.Sent);
    }

    /// <summary>
    /// O convite que nunca deveria ter sido criado também não pode ser ACEITO. Aqui o convite é
    /// plantado direto no banco (é o que sobra de um servidor que rodou sem esta guarda, ou de um
    /// convite gerado antes dela): o aceite recusa, e recusa com a MESMA cara de todas as outras
    /// recusas de convite — anti-enumeração não abre exceção nem para esta.
    /// </summary>
    [Fact]
    public async Task Aceite_DeConviteQueApontaCofrePessoal_RECUSA_SemCriarMembership()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, dono, _) = await ctx.SeedActiveUserAsync(Roles.Owner);
        UserEntity colega = await ctx.SeedAccountAsync("colega@isp.local");

        string code = RecoveryKeyCodec.Generate();
        var invite = new InviteEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = ws.Id,
            Email = colega.Email,
            Role = Roles.Operator,
            CodeHash = Sha256Hex(code),
            WrappedWkByInvite = Rand(60),
            WkVersion = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            InvitedByUserId = dono.Id,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Invites.Add(invite);
        await ctx.Db.SaveChangesAsync();

        var aceito = await ctx.Invites.AcceptAsync(
            invite.Id,
            new AcceptInviteRequest(Sha256Hex(code), Convert.ToBase64String(Rand(60))),
            colega.Id,
            default);

        Assert.Null(aceito);
        Assert.Empty(ctx.Db.Memberships.Where(m => m.UserId == colega.Id));
    }

    // ── PUT /workspaces/{id}/key ──────────────────────────────────────────────

    /// <summary>
    /// Publicar chave de TIME no cofre pessoal é recusado. Não é preciosismo: o blob guardado ali é
    /// o que o aplicativo lê para decidir "este workspace é de time", e com ele plantado o app
    /// passaria a tratar o cofre pessoal do operador — os ~700 clientes — como cofre compartilhado.
    /// </summary>
    [Fact]
    public async Task PublicarChaveDeTimeNoCofrePessoal_RECUSA_ENaoGuardaNada()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        var put = await http.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PersonalReason, body.GetProperty("reason").GetString());
        Assert.Contains("pessoal", body.GetProperty("detail").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Nada foi guardado: o cofre pessoal continua respondendo "não tenho chave de time".
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await http.GetAsync($"/workspaces/{dono.WorkspaceId}/key")).StatusCode);
    }

    // ── O time continua funcionando ───────────────────────────────────────────

    /// <summary>
    /// A guarda tem de ser CIRÚRGICA: o workspace de time aceita as duas operações como sempre. Sem
    /// esta metade, "recusar tudo" passaria nos testes de recusa e mataria o recurso inteiro.
    /// </summary>
    [Fact]
    public async Task WorkspaceDeTime_CONTINUA_AceitandoConviteEPublicacaoDeChave()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string wrapped = Convert.ToBase64String(Rand(60));
        string time = await CreateTeamAsync(http, "Clientes do ISP", wrapped);

        var convite = await http.PostAsJsonAsync($"/workspaces/{time}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Operator,
            codeHash = Sha256Hex(RecoveryKeyCodec.Generate()),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        Assert.Equal(HttpStatusCode.OK, convite.StatusCode);

        // Republicar o embrulho que já nasceu com o time é o reparo de boot: 200, sem gravar de novo.
        var put = await http.PutAsJsonAsync(
            $"/workspaces/{time}/key", new { wrappedWk = wrapped, wkVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False((await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stored").GetBoolean());
    }

    // ── O workspace que JÁ EXISTE ─────────────────────────────────────────────

    /// <summary>
    /// <b>O caso que decide o destino do acervo do operador.</b> O workspace que sai do
    /// <c>/auth/register</c> é exatamente a forma dos que JÁ EXISTEM em produção — inclusive o do
    /// operador, com os ~700 clientes. Ele é tratado como PESSOAL, recusa o convite, e o acervo
    /// continua exatamente onde estava: a recusa não move, não apaga e não esconde nada.
    /// </summary>
    [Fact]
    public async Task WorkspacePreExistente_EhPESSOAL_ERecusaSemTocarNoAcervo()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        // Um cliente do acervo, no cofre pessoal.
        var push = await http.PostAsJsonAsync("/sync/push", new
        {
            workspaceId = dono.WorkspaceId,
            changes = new[]
            {
                new
                {
                    entityType = "Asset",
                    entityId = "11111111-0000-4000-8000-000000000001",
                    operation = "created",
                    baseVersion = 0,
                    patch = new Dictionary<string, object?> { ["name"] = "OLT-Centro" },
                },
            },
        });
        push.EnsureSuccessStatusCode();

        var convite = await http.PostAsJsonAsync($"/workspaces/{dono.WorkspaceId}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Operator,
            codeHash = Sha256Hex(RecoveryKeyCodec.Generate()),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, convite.StatusCode);

        // O acervo do operador segue intocado — a guarda recusa a PORTA, não mexe no que está dentro.
        var pull = await http.GetAsync($"/sync/pull?workspaceId={dono.WorkspaceId}&cursor=0&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
        var changes = (await pull.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changes");
        Assert.Equal(1, changes.GetArrayLength());
        Assert.Equal("OLT-Centro", changes[0].GetProperty("patch").GetProperty("name").GetString());
    }

    /// <summary>
    /// <b>A marca é lista de PERMISSÃO, não de negação.</b> Um valor que este binário não conhece —
    /// uma marca criada por uma versão futura do servidor, ou uma linha adulterada — cai no lado
    /// PESSOAL, que é o lado que recusa compartilhar.
    ///
    /// <para>Escrita ao contrário (<c>kind != "personal" ⇒ é time</c>), a mesma linha autorizaria
    /// convite para qualquer valor inesperado. Errar classificando como "time" custa o acervo
    /// inteiro do dono; errar como "pessoal" custa uma recusa explicada na tela.</para>
    /// </summary>
    [Fact]
    public async Task MarcaDesconhecida_EhTratadaComoPESSOAL()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        string time = await CreateTeamAsync(http, "Clientes do ISP", Convert.ToBase64String(Rand(60)));

        // Uma marca que este binário não conhece (versão futura do servidor, linha adulterada).
        await factory.WithDbAsync(async db =>
        {
            var id = Guid.Parse(time);
            var ws = await db.Workspaces.FirstAsync(w => w.Id == id);
            ws.Kind = "grupo-federado-v2";
            await db.SaveChangesAsync();
        });

        var resp = await http.PostAsJsonAsync($"/workspaces/{time}/invites", new
        {
            email = "colega@test.local",
            role = Roles.Operator,
            codeHash = Sha256Hex(RecoveryKeyCodec.Generate()),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(
            PersonalReason,
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }

    /// <summary>
    /// <b>A ordem das guardas importa:</b> RBAC primeiro, marca de nascimento depois. Um estranho que
    /// aponta para o workspace de outra pessoa recebe 403 — o mesmo 403 de sempre — e nada na
    /// resposta diz se aquele workspace existe ou de que tipo ele é.
    ///
    /// <para>Na ordem inversa, a recusa por "cofre pessoal" viraria um oráculo: qualquer conta
    /// autenticada varreria GUIDs distinguindo "não existe / é pessoal / é time" pelo status. Quem
    /// recebe a frase completa precisa ser alguém que JÁ é membro com permissão — para esse, ela não
    /// revela nada que ele não pudesse listar.</para>
    /// </summary>
    [Fact]
    public async Task NaoMembro_RecebeAcessoNegado_ENaoDescobreOTipoDoWorkspaceAlheio()
    {
        using var factory = new CloudApiFactory();
        using var donoHttp = factory.CreateClient();
        using var estranhoHttp = factory.CreateClient();

        Account dono = await RegisterAsync(donoHttp, "dono@test.local");
        Account estranho = await RegisterAsync(estranhoHttp, "estranho@test.local");
        Auth(estranhoHttp, estranho.Token, estranho.DeviceId);

        var convite = await estranhoHttp.PostAsJsonAsync($"/workspaces/{dono.WorkspaceId}/invites", new
        {
            email = "alvo@test.local",
            role = Roles.Operator,
            codeHash = Sha256Hex(RecoveryKeyCodec.Generate()),
            wrappedWkByInvite = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
        var put = await estranhoHttp.PutAsJsonAsync(
            $"/workspaces/{dono.WorkspaceId}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });

        // O workspace do dono é PESSOAL, mas o estranho não fica sabendo disso por aqui.
        Assert.Equal(HttpStatusCode.Forbidden, convite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        foreach (var resp in new[] { convite, put })
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.TryGetProperty("reason", out _));
        }

        // E o 403 é indistinguível do de um workspace que nem existe.
        var inexistente = await estranhoHttp.PutAsJsonAsync(
            $"/workspaces/{Guid.NewGuid()}/key",
            new { wrappedWk = Convert.ToBase64String(Rand(60)), wkVersion = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, inexistente.StatusCode);
    }

    // ── Helpers (espelhos dos de TeamWorkspaceKeyApiTests) ────────────────────

    /// <summary>Cria um workspace de TIME pelo endpoint real e devolve o id gerado no cliente.</summary>
    private static async Task<string> CreateTeamAsync(HttpClient client, string name, string wrappedWk)
    {
        string id = Guid.NewGuid().ToString();
        var resp = await client.PostAsJsonAsync(
            "/workspaces", new { id, name, wrappedWk, wkVersion = 1 });
        resp.EnsureSuccessStatusCode();
        return id;
    }

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
}
