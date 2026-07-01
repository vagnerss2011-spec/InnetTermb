using RemoteOps.Desktop.NDesk;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

public sealed class LoopbackNDeskBrokerClientTests
{
    [Fact]
    public async Task CreateTicketAsync_ReturnsWaitingTicket_WithNonEmptyId()
    {
        var broker = new LoopbackNDeskBrokerClient();

        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);

        Assert.Equal("waiting", ticket.Status);
        Assert.False(string.IsNullOrWhiteSpace(ticket.Id));
        Assert.Equal("ws-local", ticket.WorkspaceId);
    }

    [Fact]
    public async Task ConnectAsync_UnknownTicket_Throws()
    {
        var broker = new LoopbackNDeskBrokerClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ConnectAsync("does-not-exist"));
    }

    [Fact]
    public async Task ConnectAsync_KnownTicket_ReturnsSessionInAwaitingConsent()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);

        var session = await broker.ConnectAsync(ticket.Id);

        Assert.Equal(NDeskSessionState.AwaitingConsent, session.State);
        Assert.Equal(ticket.Id, session.Ticket.Id);
        Assert.Equal("Operador Demo", session.ConsentRequest.OperatorDisplayName);
    }

    [Fact]
    public async Task ConnectAsync_RaisesIncomingSessionRequested_WithSameSessionInstance()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);
        INDeskAgentSession? received = null;
        broker.IncomingSessionRequested += s => received = s;

        var session = await broker.ConnectAsync(ticket.Id);

        Assert.Same(session, received);
    }
}
