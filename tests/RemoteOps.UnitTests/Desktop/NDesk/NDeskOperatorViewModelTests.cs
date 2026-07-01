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

        Assert.Equal(NDeskSessionState.Ended, vm.State);
        Assert.False(vm.EndCommand.CanExecute(null));
    }
}
