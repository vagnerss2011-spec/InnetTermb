using System;
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
}
