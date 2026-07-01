using RemoteOps.Desktop.NDesk;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

public sealed class NDeskOperatorViewModelTests
{
    [Fact]
    public void Initially_StateIsIdle_AndEndCommandDisabled()
    {
        var vm = new NDeskOperatorViewModel(new LoopbackNDeskBrokerClient());

        Assert.Equal(NDeskSessionState.Idle, vm.State);
        Assert.False(vm.EndCommand.CanExecute(null));
    }

    [Fact]
    public async Task GenerateTicketCommand_PopulatesTicketAndInput()
    {
        var vm = new NDeskOperatorViewModel(new LoopbackNDeskBrokerClient());

        vm.GenerateTicketCommand.Execute(null);
        await Task.Delay(20); // fire-and-forget command, mesmo padrão do RDP tab

        Assert.NotNull(vm.Ticket);
        Assert.Equal(vm.Ticket!.Id, vm.TicketIdInput);
    }

    [Fact]
    public async Task ConnectCommand_MovesToAwaitingConsent_NeverConnectedWithoutExternalConsent()
    {
        // Prova que a sessão não inicia sem consentimento: mesmo depois de conectar,
        // sem que o lado atendido responda, o estado nunca chega a Connected.
        var broker = new LoopbackNDeskBrokerClient();
        var vm = new NDeskOperatorViewModel(broker);
        vm.GenerateTicketCommand.Execute(null);
        await Task.Delay(20);

        vm.ConnectCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(NDeskSessionState.AwaitingConsent, vm.State);
        Assert.True(vm.EndCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectCommand_UnknownTicket_SetsErrorMessage_StaysIdle()
    {
        var vm = new NDeskOperatorViewModel(new LoopbackNDeskBrokerClient())
        {
            TicketIdInput = "does-not-exist",
        };

        vm.ConnectCommand.Execute(null);
        await Task.Delay(20);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(NDeskSessionState.Idle, vm.State);
    }

    [Fact]
    public async Task EndCommand_AfterConnect_TransitionsToEnded_AndBecomesDisabled()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var vm = new NDeskOperatorViewModel(broker);
        vm.GenerateTicketCommand.Execute(null);
        await Task.Delay(20);
        vm.ConnectCommand.Execute(null);
        await Task.Delay(20);

        vm.EndCommand.Execute(null);
        await Task.Delay(20);

        // After session ends, _session is reset to null, so State returns Idle
        Assert.Equal(NDeskSessionState.Idle, vm.State);
        Assert.False(vm.EndCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectCommand_AfterExternalConsentAccepted_ReflectsConnectedState()
    {
        // Prova que quando o lado atendido aceita (RespondConsentAsync),
        // a transição AwaitingConsent → Connected dispara OnSessionStateChanged,
        // que atualiza State, IsSessionActive e EndCommand.CanExecute.
        var broker = new LoopbackNDeskBrokerClient();
        var vm = new NDeskOperatorViewModel(broker);
        INDeskAgentSession? capturedSession = null;

        // Captura a sessão quando IncomingSessionRequested é levantado
        broker.IncomingSessionRequested += session => capturedSession = session;

        vm.GenerateTicketCommand.Execute(null);
        await Task.Delay(20);

        vm.ConnectCommand.Execute(null);
        await Task.Delay(20);

        // Neste ponto, a sessão está em AwaitingConsent
        Assert.Equal(NDeskSessionState.AwaitingConsent, vm.State);

        // Simula o lado atendido aceitando o consentimento
        Assert.NotNull(capturedSession);
        await capturedSession.RespondConsentAsync(accepted: true);
        await Task.Delay(20);

        // Agora deve estar Connected
        Assert.Equal(NDeskSessionState.Connected, vm.State);
        Assert.True(vm.IsSessionActive);
        Assert.True(vm.EndCommand.CanExecute(null));
    }

    [Fact]
    public async Task AfterSessionEnded_CanStartSecondCycle()
    {
        // Prova que após uma sessão terminar (Ended),
        // ConnectCommand.CanExecute é retomado para true (quando TicketIdInput é não-vazio),
        // permitindo um segundo ciclo ticket→connect→end sem reiniciar o app.
        var broker = new LoopbackNDeskBrokerClient();
        var vm = new NDeskOperatorViewModel(broker);

        // Primeiro ciclo
        vm.GenerateTicketCommand.Execute(null);
        await Task.Delay(20);
        var firstTicketId = vm.TicketIdInput;
        Assert.False(string.IsNullOrWhiteSpace(firstTicketId));

        vm.ConnectCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal(NDeskSessionState.AwaitingConsent, vm.State);

        vm.EndCommand.Execute(null);
        await Task.Delay(20);
        // After session ends, _session is reset to null, so State returns Idle
        Assert.Equal(NDeskSessionState.Idle, vm.State);

        // Segundo ciclo
        vm.GenerateTicketCommand.Execute(null);
        await Task.Delay(20);
        var secondTicketId = vm.TicketIdInput;
        Assert.NotEqual(firstTicketId, secondTicketId); // Novo ticket
        Assert.True(vm.ConnectCommand.CanExecute(null)); // Deve estar habilitado novamente

        vm.ConnectCommand.Execute(null);
        await Task.Delay(20);
        Assert.Equal(NDeskSessionState.AwaitingConsent, vm.State);

        vm.EndCommand.Execute(null);
        await Task.Delay(20);
        // After session ends, _session is reset to null, so State returns Idle
        Assert.Equal(NDeskSessionState.Idle, vm.State);
    }
}
