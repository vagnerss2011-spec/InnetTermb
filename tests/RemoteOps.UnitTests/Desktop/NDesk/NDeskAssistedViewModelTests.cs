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
    public void Initially_CanExecuteIsFalseWhenIdleNoSession()
    {
        var vm = new NDeskAssistedViewModel(new LoopbackNDeskBrokerClient());

        Assert.False(vm.AcceptCommand.CanExecute(null));
        Assert.False(vm.DeclineCommand.CanExecute(null));
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
    public async Task AcceptCommand_CanExecuteIsFalseAfterAccepting()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        Assert.True(assisted.AcceptCommand.CanExecute(null));
        Assert.True(assisted.DeclineCommand.CanExecute(null));

        assisted.AcceptCommand.Execute(null);
        await Task.Delay(20);

        Assert.False(assisted.AcceptCommand.CanExecute(null));
        Assert.False(assisted.DeclineCommand.CanExecute(null));
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

        // After session ends, _session is reset to null, so State returns Idle
        Assert.Equal(NDeskSessionState.Idle, assisted.State);
    }

    [Fact]
    public async Task DeclineCommand_CanExecuteIsFalseAfterDeclining()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);
        var ticket = await broker.CreateTicketAsync("ws-local", "Operador Demo", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        Assert.True(assisted.AcceptCommand.CanExecute(null));
        Assert.True(assisted.DeclineCommand.CanExecute(null));

        assisted.DeclineCommand.Execute(null);
        await Task.Delay(20);

        Assert.False(assisted.AcceptCommand.CanExecute(null));
        Assert.False(assisted.DeclineCommand.CanExecute(null));
        Assert.False(assisted.EndCommand.CanExecute(null));
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

    [Fact]
    public async Task AfterSessionEnded_CanReceiveSecondIncomingRequest()
    {
        // Prova que após uma sessão terminar (Ended),
        // a VM reseta PendingConsent e está pronta para receber um novo IncomingSessionRequested
        // sem permanecer presa ao estado da sessão anterior.
        var broker = new LoopbackNDeskBrokerClient();
        var assisted = new NDeskAssistedViewModel(broker);

        // Primeiro ciclo: criar ticket, conectar (gera IncomingSessionRequested), decline
        var ticket1 = await broker.CreateTicketAsync("ws-local", "Operador Demo 1", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket1.Id);

        Assert.True(assisted.HasPendingRequest);
        Assert.NotNull(assisted.PendingConsent);
        var firstOperatorName = assisted.PendingConsent!.OperatorDisplayName;
        Assert.Equal("Operador Demo 1", firstOperatorName);

        assisted.DeclineCommand.Execute(null);
        await Task.Delay(20);
        // After session ends, _session is reset to null, so State returns Idle
        Assert.Equal(NDeskSessionState.Idle, assisted.State);
        Assert.False(assisted.HasPendingRequest);
        Assert.Null(assisted.PendingConsent);

        // Segundo ciclo: novo ticket com operador diferente, conectar (gera novo IncomingSessionRequested)
        var ticket2 = await broker.CreateTicketAsync("ws-local", "Operador Demo 2", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket2.Id);

        Assert.True(assisted.HasPendingRequest);
        Assert.NotNull(assisted.PendingConsent);
        var secondOperatorName = assisted.PendingConsent!.OperatorDisplayName;
        Assert.Equal("Operador Demo 2", secondOperatorName);

        assisted.AcceptCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal(NDeskSessionState.Connected, assisted.State);
    }
}
