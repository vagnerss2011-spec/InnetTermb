using RemoteOps.Desktop.NDesk;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

public sealed class NDeskTabViewModelTests
{
    [Fact]
    public void Ctor_SetsTitleProtocolAndPinned()
    {
        var tab = new NDeskTabViewModel(new LoopbackNDeskBrokerClient());

        Assert.Equal("NDesk", tab.Title);
        Assert.Equal("ndesk", tab.Protocol);
        Assert.True(tab.IsPinned);
    }

    [Fact]
    public async Task OperatorAndAssisted_ShareTheSameLiveSession_EndToEnd()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var tab = new NDeskTabViewModel(broker);

        tab.Operator.GenerateTicketCommand.Execute(null);
        await Task.Delay(20);
        tab.Operator.ConnectCommand.Execute(null);
        await Task.Delay(20);

        Assert.True(tab.Assisted.HasPendingRequest);
        Assert.Equal(tab.Operator.Ticket!.Id, tab.Assisted.PendingConsent!.TicketId);

        tab.Assisted.AcceptCommand.Execute(null);
        await Task.Delay(20);

        Assert.Equal(NDeskSessionState.Connected, tab.Operator.State);
        Assert.Equal(NDeskSessionState.Connected, tab.Assisted.State);
    }
}
