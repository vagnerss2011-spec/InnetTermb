using RemoteOps.Contracts.NDesk;
using RemoteOps.Desktop.NDesk;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

public sealed class LoopbackNDeskAgentSessionTests
{
    private static LoopbackNDeskAgentSession MakeSession()
    {
        var ticket = new NDeskTicket
        {
            Id = "123456",
            WorkspaceId = "ws-local",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            Status = "waiting",
        };
        var consent = new NDeskConsentRequest("123456", "Operador Demo", "RemoteOps", "control", ["view", "control"]);
        return new LoopbackNDeskAgentSession(ticket, consent);
    }

    [Fact]
    public void NewSession_StartsInAwaitingConsent()
    {
        var session = MakeSession();
        Assert.Equal(NDeskSessionState.AwaitingConsent, session.State);
    }

    [Fact]
    public async Task RespondConsentAsync_Accepted_TransitionsToConnected()
    {
        var session = MakeSession();
        NDeskSessionState? raised = null;
        session.StateChanged += s => raised = s;

        await session.RespondConsentAsync(accepted: true);

        Assert.Equal(NDeskSessionState.Connected, session.State);
        Assert.Equal(NDeskSessionState.Connected, raised);
    }

    [Fact]
    public async Task RespondConsentAsync_Declined_TransitionsToEnded_NeverConnected()
    {
        var session = MakeSession();

        await session.RespondConsentAsync(accepted: false);

        Assert.Equal(NDeskSessionState.Ended, session.State);
    }

    [Fact]
    public async Task RespondConsentAsync_AfterAlreadyResolved_IsIgnored()
    {
        // Sessão não inicia sem consentimento — uma segunda resposta tardia (ex.: duplo
        // clique, ou o outro lado já encerrou) não pode reverter um estado já resolvido.
        var session = MakeSession();
        await session.RespondConsentAsync(accepted: false); // -> Ended

        await session.RespondConsentAsync(accepted: true); // tardia, deve ser ignorada

        Assert.Equal(NDeskSessionState.Ended, session.State);
    }

    [Fact]
    public async Task EndAsync_FromConnected_TransitionsToEnded()
    {
        var session = MakeSession();
        await session.RespondConsentAsync(accepted: true);

        await session.EndAsync();

        Assert.Equal(NDeskSessionState.Ended, session.State);
    }

    [Fact]
    public async Task EndAsync_IsIdempotent_DoesNotRaiseStateChangedTwice()
    {
        var session = MakeSession();
        await session.EndAsync();
        int raisedCount = 0;
        session.StateChanged += _ => raisedCount++;

        await session.EndAsync();

        Assert.Equal(0, raisedCount);
    }
}
