using RemoteOps.Desktop.NDesk;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

public sealed class NDeskAssistedViewModelTests
{
    [Fact]
    public void Initially_NoPendingRequest()
    {
        var vm = new NDeskAssistedViewModel(new LoopbackNDeskBrokerClient());

        Assert.False(vm.HasPendingRequest);
        Assert.Null(vm.PendingConsent);
    }

    [Fact]
    public async Task IncomingSessionRequested_PopulatesPendingConsent()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);

        await broker.ConnectAsync(ticket.Id);

        Assert.True(assisted.HasPendingRequest);
        Assert.Equal("Operador Demo", assisted.PendingConsent!.OperatorDisplayName);
        Assert.Equal("view, control", assisted.PermissionsRequestedText);
    }

    [Fact]
    public async Task AcceptCommand_TransitionsSessionToConnected()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        assisted.AcceptCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(NDeskSessionState.Connected, assisted.State);
    }

    [Fact]
    public async Task DeclineCommand_TransitionsToEnded_NeverConnected()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        assisted.DeclineCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(NDeskSessionState.Ended, assisted.State);
    }

    [Fact]
    public async Task EndCommand_OnlyEnabledWhenConnected()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        Assert.False(assisted.EndCommand.CanExecute(null));

        assisted.AcceptCommand.Execute(null);
        await Task.Delay(20);

        Assert.True(assisted.EndCommand.CanExecute(null));
    }
}
