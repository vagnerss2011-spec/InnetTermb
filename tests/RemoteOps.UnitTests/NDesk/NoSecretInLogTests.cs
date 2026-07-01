using Microsoft.EntityFrameworkCore;
using RemoteOps.NDesk.Broker.Audit;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Tickets;
using Xunit;

namespace RemoteOps.UnitTests.NDesk;

/// <summary>
/// Garante a regra do CLAUDE.md princípio 1 / docs/09 §Broker: o link token do ticket NDesk
/// nunca aparece em nenhuma mensagem de log durante emissão, redeem ou auditoria — só existe
/// em memória, na resposta HTTP de emissão.
/// </summary>
public sealed class NoSecretInLogTests
{
    [Fact]
    public async Task LinkToken_Never_Appears_In_Any_Logged_Message()
    {
        var opts = new DbContextOptionsBuilder<NDeskDbContext>()
            .UseInMemoryDatabase($"ndesk-nosecret-{Guid.NewGuid()}")
            .Options;
        using var db = new NDeskDbContext(opts);
        var clock = new FakeTimeProvider();
        var auditLogger = new CapturingLogger<NDeskAuditService>();
        var ticketLogger = new CapturingLogger<NDeskTicketService>();
        var audit = new NDeskAuditService(db, clock, auditLogger);
        var tickets = new NDeskTicketService(db, audit, clock, ticketLogger);

        var ticket = await tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), Guid.NewGuid(), "control", ["view", "control"], null, null, false, false));
        var rawToken = ticket.LinkToken!;

        await tickets.RedeemTicketAsync(rawToken);
        // Segunda tentativa (recusada) também não deve vazar o token em log.
        await tickets.RedeemTicketAsync(rawToken);

        foreach (var message in auditLogger.Messages.Concat(ticketLogger.Messages))
            Assert.DoesNotContain(rawToken, message);
    }

    [Fact]
    public async Task Persisted_Hash_Does_Not_Equal_Raw_Token()
    {
        var opts = new DbContextOptionsBuilder<NDeskDbContext>()
            .UseInMemoryDatabase($"ndesk-nosecret-{Guid.NewGuid()}")
            .Options;
        using var db = new NDeskDbContext(opts);
        var clock = new FakeTimeProvider();
        var audit = new NDeskAuditService(db, clock, new CapturingLogger<NDeskAuditService>());
        var tickets = new NDeskTicketService(db, audit, clock, new CapturingLogger<NDeskTicketService>());

        var ticket = await tickets.IssueTicketAsync(new IssueTicketRequest(
            Guid.NewGuid(), null, "basic", ["view"], null, null, false, false));

        var entity = await db.Tickets.FindAsync(Guid.Parse(ticket.Id));
        Assert.NotEqual(ticket.LinkToken, entity!.LinkTokenHash);
    }
}
