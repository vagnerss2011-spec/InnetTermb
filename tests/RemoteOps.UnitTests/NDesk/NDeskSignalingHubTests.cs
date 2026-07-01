using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.NDesk.Broker.Consent;
using RemoteOps.NDesk.Broker.Signaling;
using RemoteOps.NDesk.Broker.Tickets;
using RemoteOps.UnitTests.NDesk.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.NDesk;

/// <summary>
/// Recusa de sessão sem grant — a garantia mais crítica do broker: o Hub nunca repassa
/// SDP/ICE sem consentimento válido, mesmo que ambos os lados já estejam no mesmo grupo.
/// </summary>
public sealed class NDeskSignalingHubTests
{
    private static NDeskSignalingHub CreateHub(NDeskTestContext ctx, FakeHubCallerClients clients) => new(
        ctx.Tickets, ctx.Grants, NullLogger<NDeskSignalingHub>.Instance)
    {
        Context = new FakeHubCallerContext(),
        Clients = clients,
    };

    [Fact]
    public async Task SendSignal_Without_Grant_Throws_And_Never_Relays()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "control", ["view", "control"], null, null, false, false));
        var redeemed = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        var sessionId = redeemed.SessionId!.Value;

        var clients = new FakeHubCallerClients();
        var hub = CreateHub(ctx, clients);

        await Assert.ThrowsAsync<HubException>(() =>
            hub.SendSignal(sessionId.ToString(), "sdp-offer", "opaque-payload"));

        Assert.Empty(clients.GroupSends);
    }

    [Fact]
    public async Task SendSignal_With_Valid_Grant_Relays_Opaque_Payload_To_Group()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "control", ["view", "control"], null, null, false, false));
        var redeemed = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        var sessionId = redeemed.SessionId!.Value;
        await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", null, "PC-01"), "control", ["view", "control"], null, null));

        var clients = new FakeHubCallerClients();
        var hub = CreateHub(ctx, clients);

        await hub.SendSignal(sessionId.ToString(), "sdp-offer", "opaque-payload");

        var sent = Assert.Single(clients.GroupSends);
        Assert.Equal(sessionId.ToString(), sent.Group);
        Assert.Equal("Signal", sent.Method);
        Assert.Equal(["sdp-offer", "opaque-payload"], sent.Args);
    }

    [Fact]
    public async Task SendSignal_After_Revoke_Is_Refused_Again()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "control", ["view", "control"], null, null, false, false));
        var redeemed = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        var sessionId = redeemed.SessionId!.Value;
        await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", null, "PC-01"), "control", ["view", "control"], null, null));
        await ctx.Grants.RevokeConsentAsync(sessionId, "assisted-user");

        var clients = new FakeHubCallerClients();
        var hub = CreateHub(ctx, clients);

        await Assert.ThrowsAsync<HubException>(() =>
            hub.SendSignal(sessionId.ToString(), "ice-candidate", "opaque-payload"));

        Assert.Empty(clients.GroupSends);
    }

    // Regressão (descoberta pelo verificador de integração tools/ndesk-signaling-check): o Hub
    // lia só o claim "sub", mas o middleware JWT o mapeia para ClaimTypes.NameIdentifier
    // (MapInboundClaims=true), então com um JWT real o operador era sempre recusado. Estes testes
    // exercitam JoinSession pela mesma leitura dos endpoints REST (NameIdentifier).
    [Fact]
    public async Task JoinSession_Operator_WithMappedNameIdentifierClaim_IsAuthorized()
    {
        using var ctx = new NDeskTestContext();
        var operatorId = Guid.NewGuid();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), operatorId, "control", ["view", "control"], null, null, false, false));
        var sessionId = (await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!)).SessionId!.Value;

        var groups = new FakeGroupManager();
        var hub = new NDeskSignalingHub(ctx.Tickets, ctx.Grants, NullLogger<NDeskSignalingHub>.Instance)
        {
            Context = FakeHubCallerContext.AuthenticatedOperator(operatorId),
            Clients = new FakeHubCallerClients(),
            Groups = groups,
        };

        await hub.JoinSession(sessionId.ToString(), "operator"); // não deve lançar

        var added = Assert.Single(groups.Added);
        Assert.Equal(sessionId.ToString(), added.Group);
    }

    [Fact]
    public async Task JoinSession_Operator_WithWrongUserId_Throws()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), Guid.NewGuid(), "control", ["view", "control"], null, null, false, false));
        var sessionId = (await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!)).SessionId!.Value;

        var hub = new NDeskSignalingHub(ctx.Tickets, ctx.Grants, NullLogger<NDeskSignalingHub>.Instance)
        {
            Context = FakeHubCallerContext.AuthenticatedOperator(Guid.NewGuid()), // id que não criou o ticket
            Clients = new FakeHubCallerClients(),
            Groups = new FakeGroupManager(),
        };

        await Assert.ThrowsAsync<HubException>(() => hub.JoinSession(sessionId.ToString(), "operator"));
    }
}
