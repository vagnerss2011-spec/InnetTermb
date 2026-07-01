using RemoteOps.NDesk.Broker.Tickets;
using Xunit;

namespace RemoteOps.UnitTests.NDesk;

public sealed class NDeskTicketServiceTests
{
    // ── Emissão ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueTicket_Returns_WaitingStatus_With_LinkToken()
    {
        using var ctx = new NDeskTestContext();
        var workspaceId = Guid.NewGuid();

        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            WorkspaceId: workspaceId,
            CreatedByUserId: Guid.NewGuid(),
            RequestedMode: "control",
            PermissionsRequested: ["view", "control"],
            Ttl: null,
            AgentMinimumWindows: "10",
            AgentAllowWindows7Legacy: false,
            AgentRequiresInstall: false));

        Assert.Equal("waiting", ticket.Status);
        Assert.False(string.IsNullOrEmpty(ticket.LinkToken));
        Assert.Equal(workspaceId.ToString(), ticket.WorkspaceId);
        Assert.Equal(["view", "control"], ticket.PermissionsRequested);
    }

    [Fact]
    public async Task IssueTicket_Applies_Default_Ttl_When_Not_Specified()
    {
        using var ctx = new NDeskTestContext();

        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], null, null, false, false));

        var expectedExpiry = ctx.Clock.UtcNowValue.Add(NDeskTicketService.DefaultTtl);
        Assert.Equal(expectedExpiry, ticket.ExpiresAt);
    }

    [Fact]
    public async Task IssueTicket_Clamps_Ttl_Above_Maximum_To_Default()
    {
        using var ctx = new NDeskTestContext();

        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], TimeSpan.FromHours(6), null, false, false));

        var expectedExpiry = ctx.Clock.UtcNowValue.Add(NDeskTicketService.DefaultTtl);
        Assert.Equal(expectedExpiry, ticket.ExpiresAt);
    }

    // ── Single-use ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Redeem_Valid_Ticket_Marks_Connected_And_Is_SingleUse()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], null, null, false, false));

        var first = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        Assert.Equal(RedeemOutcome.Success, first.Outcome);
        Assert.NotNull(first.SessionId);
        Assert.Equal("connected", first.Ticket!.Status);

        // Segunda tentativa com o MESMO link token deve ser recusada — regra de uso único.
        var second = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);
        Assert.Equal(RedeemOutcome.AlreadyUsed, second.Outcome);
        Assert.Null(second.SessionId);
    }

    [Fact]
    public async Task Redeem_Unknown_Token_Returns_NotFound()
    {
        using var ctx = new NDeskTestContext();

        var result = await ctx.Tickets.RedeemTicketAsync("token-que-nunca-foi-emitido");

        Assert.Equal(RedeemOutcome.NotFound, result.Outcome);
        Assert.Null(result.SessionId);
    }

    // ── Expiração ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Redeem_Expired_Ticket_Is_Refused()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], TimeSpan.FromMinutes(5), null, false, false));

        ctx.Clock.UtcNowValue = ctx.Clock.UtcNowValue.AddMinutes(6);

        var result = await ctx.Tickets.RedeemTicketAsync(ticket.LinkToken!);

        Assert.Equal(RedeemOutcome.Expired, result.Outcome);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public async Task GetStatus_Reflects_Expiration_Lazily()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], TimeSpan.FromMinutes(1), null, false, false));

        ctx.Clock.UtcNowValue = ctx.Clock.UtcNowValue.AddMinutes(2);

        var status = await ctx.Tickets.GetStatusAsync(Guid.Parse(ticket.Id));

        Assert.Equal("expired", status!.Status);
    }

    // ── Segurança ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_Never_Returns_LinkToken()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], null, null, false, false));

        var status = await ctx.Tickets.GetStatusAsync(Guid.Parse(ticket.Id));

        Assert.Null(status!.LinkToken);
    }

    [Fact]
    public async Task Persisted_Entity_Never_Stores_Raw_LinkToken()
    {
        using var ctx = new NDeskTestContext();
        var ticket = await ctx.Tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], null, null, false, false));

        var entity = await ctx.Db.Tickets.FindAsync(Guid.Parse(ticket.Id));

        Assert.NotEqual(ticket.LinkToken, entity!.LinkTokenHash);
        Assert.Equal(64, entity.LinkTokenHash.Length); // hex SHA-256
    }
}
