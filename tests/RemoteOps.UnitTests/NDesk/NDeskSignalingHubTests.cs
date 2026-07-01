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
}
