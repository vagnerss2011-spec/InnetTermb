using RemoteOps.NDesk.Broker.Consent;
using RemoteOps.NDesk.Broker.Tickets;
using Xunit;

namespace RemoteOps.UnitTests.NDesk;

/// <summary>
/// Prova a regra inegociável do CLAUDE.md/docs/09: nenhuma sessão é autorizada a trocar
/// signaling sem um consentimento explícito, não-revogado e não-expirado.
/// </summary>
public sealed class NDeskPermissionGrantServiceTests
{
    private static async Task<Guid> IssueAndRedeemAsync(NDeskTestContext ctx, string mode, params string[] permissions)
    {
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, mode, [.. permissions], null, null, false, false));
        var redeemed = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        return redeemed.SessionId!.Value;
    }

    [Fact]
    public async Task Session_Is_Not_Authorized_Before_Consent()
    {
        using var ctx = new NDeskTestContext();
        var sessionId = await IssueAndRedeemAsync(ctx, "control", "view", "control");

        var authorized = await ctx.Grants.IsSessionAuthorizedAsync(sessionId);

        Assert.False(authorized, "Sessão recém-conectada, sem grant, nunca deve estar autorizada.");
    }

    [Fact]
    public async Task GrantConsent_Without_Redeemed_Ticket_Is_Refused()
    {
        using var ctx = new NDeskTestContext();
        var sessionId = Guid.NewGuid(); // nunca passou por RedeemTicketAsync

        var result = await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", null, "PC-01"), "basic", ["view"], null, null));

        Assert.Equal(GrantOutcome.NoActiveTicket, result.Outcome);
        Assert.False(await ctx.Grants.IsSessionAuthorizedAsync(sessionId));
    }

    [Fact]
    public async Task Session_Is_Authorized_After_Consent_Granted()
    {
        using var ctx = new NDeskTestContext();
        var sessionId = await IssueAndRedeemAsync(ctx, "control", "view", "control");

        var result = await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", "DOMAIN\\fulano", "PC-01"), "control", ["view", "control"], null, "v1"));

        Assert.Equal(GrantOutcome.Granted, result.Outcome);
        Assert.True(await ctx.Grants.IsSessionAuthorizedAsync(sessionId));
    }

    [Fact]
    public async Task GrantConsent_Cannot_Exceed_Requested_Permissions()
    {
        using var ctx = new NDeskTestContext();
        // Convite só solicitou "view".
        var sessionId = await IssueAndRedeemAsync(ctx, "basic", "view");

        var result = await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", null, "PC-01"), "basic", ["view", "control"], null, null));

        Assert.Equal(GrantOutcome.PermissionsExceedRequest, result.Outcome);
        Assert.False(await ctx.Grants.IsSessionAuthorizedAsync(sessionId));
    }

    [Fact]
    public async Task Revoke_Consent_Deauthorizes_Session_Immediately()
    {
        using var ctx = new NDeskTestContext();
        var sessionId = await IssueAndRedeemAsync(ctx, "control", "view", "control");
        await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", null, "PC-01"), "control", ["view", "control"], null, null));

        await ctx.Grants.RevokeConsentAsync(sessionId, "assisted-user");

        Assert.False(await ctx.Grants.IsSessionAuthorizedAsync(sessionId));
    }

    [Fact]
    public async Task Expired_Grant_Deauthorizes_Session()
    {
        using var ctx = new NDeskTestContext();
        var sessionId = await IssueAndRedeemAsync(ctx, "control", "view", "control");
        await ctx.Grants.GrantConsentAsync(new GrantConsentRequest(
            sessionId, new GrantedBy("Fulano", null, "PC-01"), "control", ["view", "control"],
            TimeSpan.FromMinutes(30), null));

        ctx.Clock.UtcNowValue = ctx.Clock.UtcNowValue.AddMinutes(31);

        Assert.False(await ctx.Grants.IsSessionAuthorizedAsync(sessionId));
    }

    [Fact]
    public async Task DenyConsent_Closes_Ticket_As_Denied()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], null, null, false, false));
        var redeemed = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        var sessionId = redeemed.SessionId!.Value;

        await ctx.Grants.DenyConsentAsync(sessionId, "usuário recusou");

        var status = await ctx.Tickets.GetStatusAsync(Guid.Parse(ticket.Id));
        Assert.Equal("denied", status!.Status);
        Assert.False(await ctx.Grants.IsSessionAuthorizedAsync(sessionId));
    }
}
