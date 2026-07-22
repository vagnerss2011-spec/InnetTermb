using System;
using System.Collections.Generic;
using System.Text.Json;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Contrato de fio entre o cliente e os endpoints de TIME do backend (Fatia 1, estágio 1c).
///
/// <para>Mesma razão dos <see cref="AccountContractsWireTests"/>/<see cref="SecretsContractsWireTests"/>:
/// o cliente não referencia o assembly do servidor, então o que amarra os dois é este teste. Os
/// records "Server*" abaixo são cópia literal de
/// <c>src/RemoteOps.Cloud/Teams/TeamModels.cs</c> — se um campo for renomeado lá, é AQUI que
/// aparece, e não num 400 no PC do colega.</para>
/// </summary>
public sealed class TeamContractsWireTests
{
    private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);

    // ── Espelho do backend (RemoteOps.Cloud/Teams/TeamModels.cs) ─────────────────────────

    private sealed record ServerCreateInviteRequest(
        string Email, string Role, string CodeHash, string WrappedWkByInvite, int WkVersion);

    private sealed record ServerCreateInviteResponse(
        string InviteId, string Email, string Role, DateTimeOffset ExpiresAt, bool EmailDelivered);

    private sealed record ServerInviteContextRequest(string CodeHash);

    private sealed record ServerInviteContextResponse(
        string WorkspaceId, string WorkspaceName, string Role, string WrappedWkByInvite, int WkVersion);

    private sealed record ServerAcceptInviteRequest(string CodeHash, string WrappedWk);

    private sealed record ServerAcceptInviteResponse(
        string WorkspaceId, string WorkspaceName, string Role, int WkVersion, bool SessionRefreshRequired);

    private sealed record ServerWorkspaceKeyResponse(string WorkspaceId, string WrappedWk, int WkVersion);

    private sealed record ServerPublishWorkspaceKeyRequest(string WrappedWk, int WkVersion);

    private sealed record ServerPublishWorkspaceKeyResponse(string WorkspaceId, bool Stored, int WkVersion);

    private sealed record ServerTeamMember(
        string UserId, string Email, string DisplayName, string Role, bool HasWk, int WkVersion);

    private sealed record ServerTeamMembersResponse(IReadOnlyList<ServerTeamMember> Members);

    private sealed record ServerCreateWorkspaceRequest(string Id, string Name, string WrappedWk, int WkVersion);

    private sealed record ServerCreateWorkspaceResponse(string Id, string Name, string Role);

    // ── POST /workspaces (1g) ────────────────────────────────────────────────────────────

    /// <summary>
    /// A criação do TIME cai inteira na forma do servidor. O campo que mais importa é o <c>id</c>:
    /// ele é sorteado no CLIENTE porque o AAD do embrulho da WK é <c>"wk|time:{id}"</c> — a chave não
    /// existe antes dele. Se este campo se perdesse na travessia, o servidor geraria outro id, e o
    /// embrulho que subiu no mesmo pedido não abriria mais.
    /// </summary>
    [Fact]
    public void CriarTime_CaiInteiroNaFormaDoServidor()
    {
        var client = new CreateTeamWorkspaceRequest(
            "8f3b6f4a-0000-4000-8000-000000000001", "Clientes do ISP", "YmxvYmJsb2JibG9i", 1);

        var server = JsonSerializer.Deserialize<ServerCreateWorkspaceRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.NotNull(server);
        Assert.Equal(client.Id, server.Id);
        Assert.Equal(client.Name, server.Name);
        Assert.Equal(client.WrappedWk, server.WrappedWk);
        Assert.Equal(client.WkVersion, server.WkVersion);
    }

    [Fact]
    public void RespostaDaCriacaoDoTime_EhLidaPeloCliente()
    {
        var server = new ServerCreateWorkspaceResponse(
            "8f3b6f4a-0000-4000-8000-000000000001", "Clientes do ISP", "Owner");

        var client = JsonSerializer.Deserialize<CreateTeamWorkspaceResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        Assert.NotNull(client);
        Assert.Equal(server.Id, client.Id);
        Assert.Equal(server.Name, client.Name);
        Assert.Equal(server.Role, client.Role);
    }

    // ── POST /workspaces/{id}/invites ────────────────────────────────────────────────────

    [Fact]
    public void CriarConvite_CaiInteiroNaFormaDoServidor()
    {
        var client = new CreateTeamInviteRequest(
            "colega@innet.tec.br", "Manager", new string('a', 64), "YmxvYg==", 1);

        var server = JsonSerializer.Deserialize<ServerCreateInviteRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.NotNull(server);
        Assert.Equal(client.Email, server.Email);
        Assert.Equal(client.Role, server.Role);
        Assert.Equal(client.CodeHash, server.CodeHash);
        Assert.Equal(client.WrappedWkByInvite, server.WrappedWkByInvite);
        Assert.Equal(client.WkVersion, server.WkVersion);
    }

    /// <summary>
    /// O corpo do convite NÃO tem campo de código. É guarda de contrato com peso de segurança: um
    /// campo "code" aparecendo aqui um dia significaria a chave do time indo para o servidor.
    /// </summary>
    [Fact]
    public void CorpoDoConvite_NaoTemCampoDeCodigo()
    {
        string json = JsonSerializer.Serialize(
            new CreateTeamInviteRequest("a@b.c", "Manager", new string('a', 64), "YmxvYg==", 1), s_web);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("code", out _));
        Assert.False(doc.RootElement.TryGetProperty("inviteCode", out _));
        Assert.True(doc.RootElement.TryGetProperty("codeHash", out _));
    }

    [Fact]
    public void RespostaDoConvite_EhLidaPeloCliente()
    {
        var server = new ServerCreateInviteResponse(
            "3c9d1a2b-0000-4000-8000-000000000002", "colega@innet.tec.br", "Manager",
            DateTimeOffset.UtcNow.AddDays(7), EmailDelivered: false);

        var client = JsonSerializer.Deserialize<CreateTeamInviteResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        Assert.NotNull(client);
        Assert.Equal(server.InviteId, client.InviteId);
        Assert.Equal(server.ExpiresAt, client.ExpiresAt);
        Assert.False(client.EmailDelivered); // o aviso de e-mail não entregue tem de chegar à tela
    }

    // ── POST /invites/{id}/context e /accept ─────────────────────────────────────────────

    [Fact]
    public void ContextoDoConvite_VaiEVoltaNaFormaDoServidor()
    {
        var request = new TeamInviteContextRequest(new string('b', 64));
        var serverRequest = JsonSerializer.Deserialize<ServerInviteContextRequest>(
            JsonSerializer.Serialize(request, s_web), s_web);
        Assert.Equal(request.CodeHash, serverRequest!.CodeHash);

        var serverResponse = new ServerInviteContextResponse(
            "8f3b6f4a-0000-4000-8000-000000000001", "Innet Telecom", "Manager", "YmxvYg==", 1);
        var clientResponse = JsonSerializer.Deserialize<TeamInviteContextResponse>(
            JsonSerializer.Serialize(serverResponse, s_web), s_web);

        Assert.NotNull(clientResponse);
        Assert.Equal(serverResponse.WorkspaceId, clientResponse.WorkspaceId);
        Assert.Equal(serverResponse.WorkspaceName, clientResponse.WorkspaceName);
        Assert.Equal(serverResponse.WrappedWkByInvite, clientResponse.WrappedWkByInvite);
        Assert.Equal(serverResponse.WkVersion, clientResponse.WkVersion);
    }

    [Fact]
    public void AceiteDoConvite_VaiEVoltaNaFormaDoServidor()
    {
        var request = new AcceptTeamInviteRequest(new string('c', 64), "YmxvYg==");
        var serverRequest = JsonSerializer.Deserialize<ServerAcceptInviteRequest>(
            JsonSerializer.Serialize(request, s_web), s_web);
        Assert.Equal(request.CodeHash, serverRequest!.CodeHash);
        Assert.Equal(request.WrappedWk, serverRequest.WrappedWk);

        var serverResponse = new ServerAcceptInviteResponse(
            "8f3b6f4a-0000-4000-8000-000000000001", "Innet Telecom", "Manager", 1, SessionRefreshRequired: true);
        var clientResponse = JsonSerializer.Deserialize<AcceptTeamInviteResponse>(
            JsonSerializer.Serialize(serverResponse, s_web), s_web);

        Assert.NotNull(clientResponse);
        Assert.Equal(serverResponse.Role, clientResponse.Role);
        Assert.Equal(serverResponse.WkVersion, clientResponse.WkVersion);

        // Silenciar este aviso deixaria o cofre do time respondendo 403 "sem motivo" até o token expirar.
        Assert.True(clientResponse.SessionRefreshRequired);
    }

    // ── GET /workspaces/{id}/key ─────────────────────────────────────────────────────────

    [Fact]
    public void ChaveDoWorkspace_EhLidaPeloCliente()
    {
        var server = new ServerWorkspaceKeyResponse(
            "8f3b6f4a-0000-4000-8000-000000000001", "YmxvYg==", 1);

        var client = JsonSerializer.Deserialize<TeamWorkspaceKeyResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        Assert.NotNull(client);
        Assert.Equal(server.WorkspaceId, client.WorkspaceId);
        Assert.Equal(server.WrappedWk, client.WrappedWk);
        Assert.Equal(server.WkVersion, client.WkVersion);
    }

    // ── PUT /workspaces/{id}/key ─────────────────────────────────────────────────────────

    /// <summary>
    /// O corpo da publicação cai inteiro na forma do servidor. É o pedido que faz o embrulho do DONO
    /// existir no servidor sem depender de convite nenhum — se um campo se perder na travessia, o
    /// dono volta a ficar sem chave guardada e o segundo computador dele sorteia outra.
    /// </summary>
    [Fact]
    public void PublicarChave_CaiInteiroNaFormaDoServidor()
    {
        var client = new PublishTeamWorkspaceKeyRequest("YmxvYmJsb2JibG9i", 1);

        var server = JsonSerializer.Deserialize<ServerPublishWorkspaceKeyRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.NotNull(server);
        Assert.Equal(client.WrappedWk, server.WrappedWk);
        Assert.Equal(client.WkVersion, server.WkVersion);
    }

    /// <summary>
    /// <b>O pedido não tem usuário-alvo.</b> Guarda de contrato com peso de segurança: um campo
    /// "userId" aqui um dia significaria um membro gravando o embrulho de OUTRO — e o colega
    /// deixaria de abrir o cofre no próximo computador dele, sem nada na tela.
    /// </summary>
    [Fact]
    public void CorpoDaPublicacao_NaoTemUsuarioAlvo()
    {
        string json = JsonSerializer.Serialize(new PublishTeamWorkspaceKeyRequest("YmxvYmJsb2JibG9i", 1), s_web);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("userId", out _));
        Assert.False(doc.RootElement.TryGetProperty("targetUserId", out _));
        Assert.True(doc.RootElement.TryGetProperty("wrappedWk", out _));
    }

    /// <summary>
    /// A resposta é lida pelo cliente, com <c>stored</c> inteiro: <c>false</c> é o caminho normal do
    /// reparo de boot (já estava lá) e <c>true</c> é a primeira publicação. Perder esse bit faria o
    /// app não distinguir "acabei de fechar o buraco" de "não tinha nada a fazer".
    /// </summary>
    [Fact]
    public void RespostaDaPublicacao_EhLidaPeloCliente()
    {
        var server = new ServerPublishWorkspaceKeyResponse(
            "8f3b6f4a-0000-4000-8000-000000000001", Stored: true, 1);

        var client = JsonSerializer.Deserialize<PublishTeamWorkspaceKeyResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        Assert.NotNull(client);
        Assert.Equal(server.WorkspaceId, client.WorkspaceId);
        Assert.True(client.Stored);
        Assert.Equal(server.WkVersion, client.WkVersion);
    }

    // ── GET /workspaces/{id}/members (1e) ────────────────────────────────────────────────

    /// <summary>
    /// A lista de membros cai inteira na forma do cliente. <c>hasWk</c> é o campo que a tela usa
    /// para marcar quem ainda não tem a chave do time: se ele se perder na travessia, o
    /// <c>bool</c> vira <c>false</c> em silêncio e o time inteiro apareceria como "sem chave".
    /// </summary>
    [Fact]
    public void ListaDeMembros_EhLidaPeloCliente()
    {
        var server = new ServerTeamMembersResponse(
        [
            new("11111111-0000-4000-8000-000000000001", "dono@innet.tec.br", "Vagner", "Owner", true, 1),
            new("11111111-0000-4000-8000-000000000002", "novo@innet.tec.br", "Ana", "Operator", false, 1),
        ]);

        var client = JsonSerializer.Deserialize<TeamMembersResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        Assert.NotNull(client);
        Assert.Equal(2, client.Members.Count);
        Assert.Equal(server.Members[0].UserId, client.Members[0].UserId);
        Assert.Equal(server.Members[0].Email, client.Members[0].Email);
        Assert.Equal(server.Members[0].DisplayName, client.Members[0].DisplayName);
        Assert.Equal(server.Members[0].Role, client.Members[0].Role);
        Assert.True(client.Members[0].HasWk);

        // O membro sem chave TEM de chegar marcado — é ele que enxerga a lista e não abre senha.
        Assert.False(client.Members[1].HasWk);
        Assert.Equal(1, client.Members[1].WkVersion);
    }

    /// <summary>
    /// A lista NÃO carrega o embrulho da chave de ninguém. É guarda com peso de segurança: o
    /// <c>WrappedWk</c> de um membro só abre com a AMK dele, e distribuí-lo aos outros só aumentaria
    /// a superfície sem servir a ninguém.
    /// </summary>
    [Fact]
    public void ListaDeMembros_NaoCarregaEmbrulhoDeChave()
    {
        string json = JsonSerializer.Serialize(
            new TeamMembersResponse([new("u1", "a@b.c", "A", "Owner", true, 1)]), s_web);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement member = doc.RootElement.GetProperty("members")[0];

        Assert.False(member.TryGetProperty("wrappedWk", out _));
        Assert.False(member.TryGetProperty("wrappedWkByInvite", out _));
        Assert.True(member.TryGetProperty("hasWk", out _));
    }
}
