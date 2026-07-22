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
/// O <b>cliente real</b> (<see cref="TeamApiClient"/>) contra o <b>backend real</b>, sobre HTTP: é a
/// única forma de provar rota, verbo e — principalmente — o significado de cada status code.
///
/// <para><b>Por que não bastava um teste de VM com fake:</b> o desfecho da remoção não vem no corpo,
/// vem no STATUS (204 / 404 / 409). Um fake devolvendo o enum já pronto testaria a minha suposição
/// sobre o servidor, não o servidor. E o custo de errar aqui é direto na tela: "removi" para quem
/// nunca foi membro, ou "não foi possível" para o último dono — que é justamente o caso em que o
/// operador precisa saber o que fazer.</para>
/// </summary>
public sealed class TeamMembersApiTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TeamApiClient ClientFor(HttpClient http, Account account) =>
        new(http, Guid.Parse(account.DeviceId),
            new FakeTokenStore(new TokenSet(account.Token, account.RefreshToken, DateTimeOffset.UtcNow.AddHours(1))));

    /// <summary>
    /// A lista de membros vale em QUALQUER workspace, inclusive no cofre pessoal — a tela de Equipe
    /// abre com o cofre pessoal ativo e precisa mostrar algo verdadeiro em vez de erro. Aqui é o
    /// pessoal de propósito: ele nunca tem chave de time, e o <c>hasWk: false</c> é o bit que impede
    /// a tela de prometer cofre compartilhado a quem não tem chave nenhuma.
    /// </summary>
    [Fact]
    public async Task ListarMembros_SobreHttp_TrazODono()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");

        TeamApiClient api = ClientFor(http, dono);
        TeamMembersResponse members = await api.GetMembersAsync(dono.WorkspaceId);

        TeamMemberDto only = Assert.Single(members.Members);
        Assert.Equal("dono@test.local", only.Email);
        Assert.Equal(Roles.Owner, only.Role);

        // Cofre pessoal não tem chave de time — e o servidor agora recusa qualquer tentativa de
        // plantar uma aqui (PUT /key devolve 422). A tela precisa desse bit.
        Assert.False(only.HasWk);
    }

    /// <summary>
    /// Round-trip inteiro pelo cliente: convite → aceite → o colega aparece na lista COM chave →
    /// remoção devolve <see cref="TeamMemberRemoval.Removed"/> e ele some.
    /// </summary>
    [Fact]
    public async Task Remover_SobreHttp_CortaOAcesso_EODesfechoChegaAoCliente()
    {
        using var factory = new CloudApiFactory();
        using var ownerHttp = factory.CreateClient();
        using var inviteeHttp = factory.CreateClient();

        Account dono = await RegisterAsync(ownerHttp, "dono@test.local");
        Account colega = await RegisterAsync(inviteeHttp, "colega@test.local");
        Auth(ownerHttp, dono.Token, dono.DeviceId);
        Auth(inviteeHttp, colega.Token, colega.DeviceId);

        // Convite só existe para TIME: o servidor recusa convite no cofre pessoal do dono — e é bom
        // que recuse, porque quem aceitasse baixaria o acervo inteiro dele.
        string time = await CreateTeamAsync(ownerHttp, "Clientes do ISP");

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

        var accept = await inviteeHttp.PostAsJsonAsync($"/invites/{inviteId}/accept", new
        {
            codeHash = Sha256Hex(code),
            wrappedWk = Convert.ToBase64String(Rand(60)),
        });
        accept.EnsureSuccessStatusCode();

        TeamApiClient api = ClientFor(ownerHttp, dono);
        TeamMembersResponse antes = await api.GetMembersAsync(time);
        TeamMemberDto entrante = Assert.Single(antes.Members, m => m.Email == "colega@test.local");
        Assert.True(entrante.HasWk);

        Assert.Equal(
            TeamMemberRemoval.Removed,
            await api.RemoveMemberAsync(time, entrante.UserId));

        TeamMembersResponse depois = await api.GetMembersAsync(time);
        Assert.DoesNotContain(depois.Members, m => m.Email == "colega@test.local");
    }

    /// <summary>
    /// Quem nunca foi membro devolve <see cref="TeamMemberRemoval.NotAMember"/>, e não "removido".
    /// A tela precisa recarregar e dizer a verdade — dizer "removi" seria mentira no exato assunto
    /// em que o operador vai basear a decisão de trocar (ou não) as senhas dos equipamentos.
    /// </summary>
    [Fact]
    public async Task RemoverQuemNaoEMembro_NaoDizQueRemoveu()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");

        TeamApiClient api = ClientFor(http, dono);

        Assert.Equal(
            TeamMemberRemoval.NotAMember,
            await api.RemoveMemberAsync(dono.WorkspaceId, Guid.NewGuid().ToString()));
    }

    /// <summary>
    /// Último dono: o 409 do servidor chega ao cliente como <see cref="TeamMemberRemoval.LastOwner"/>
    /// — um desfecho próprio, porque a saída é promover outra pessoa, não "tentar de novo".
    /// </summary>
    [Fact]
    public async Task RemoverOUltimoDono_ChegaComoDesfechoProprio()
    {
        using var factory = new CloudApiFactory();
        using var http = factory.CreateClient();
        Account dono = await RegisterAsync(http, "dono@test.local");
        Auth(http, dono.Token, dono.DeviceId);

        TeamApiClient api = ClientFor(http, dono);
        TeamMembersResponse membros = await api.GetMembersAsync(dono.WorkspaceId);
        string donoId = membros.Members[0].UserId;

        Assert.Equal(
            TeamMemberRemoval.LastOwner,
            await api.RemoveMemberAsync(dono.WorkspaceId, donoId));

        // E ele continua lá: um "desfecho" que apagasse o dono seria pior que um erro.
        Assert.Single((await api.GetMembersAsync(dono.WorkspaceId)).Members);
    }

    /// <summary>
    /// Listar o time de quem NÃO é membro estoura. Devolver lista vazia aqui seria o pior desfecho
    /// possível: a tela desenharia "este time não tem ninguém" para um workspace alheio.
    /// </summary>
    [Fact]
    public async Task ListarTimeAlheio_ESTOURA_EmVezDeDevolverListaVazia()
    {
        using var factory = new CloudApiFactory();
        using var donoHttp = factory.CreateClient();
        using var estranhoHttp = factory.CreateClient();

        Account dono = await RegisterAsync(donoHttp, "dono@test.local");
        Account estranho = await RegisterAsync(estranhoHttp, "estranho@test.local");

        TeamApiClient api = ClientFor(estranhoHttp, estranho);

        var ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => api.GetMembersAsync(dono.WorkspaceId));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    // ── Helpers (espelhos dos de TeamApiTests) ────────────────────────────────

    /// <summary>Cria um workspace de TIME pelo endpoint real e devolve o id sorteado no cliente.</summary>
    private static async Task<string> CreateTeamAsync(HttpClient client, string name)
    {
        var id = Guid.NewGuid().ToString();
        var resp = await client.PostAsJsonAsync("/workspaces", new
        {
            id,
            name,
            wrappedWk = Convert.ToBase64String(Rand(60)),
            wkVersion = 1,
        });
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
