using RemoteOps.Contracts.NDesk;

namespace RemoteOps.Desktop.NDesk;

/// <summary>
/// Sessão fake in-memory. A única forma de chegar a Connected é via
/// RespondConsentAsync(true) partindo de AwaitingConsent — não existe setter público
/// de estado, então o consentimento não pode ser burlado (CLAUDE.md princípio 3).
/// </summary>
public sealed class LoopbackNDeskAgentSession : INDeskAgentSession
{
    private readonly object _lock = new();
    private NDeskSessionState _state = NDeskSessionState.AwaitingConsent;

    public LoopbackNDeskAgentSession(NDeskTicket ticket, NDeskConsentRequest consentRequest)
    {
        SessionId = Guid.NewGuid().ToString("n");
        Ticket = ticket;
        ConsentRequest = consentRequest;
    }

    public string SessionId { get; }

    public NDeskTicket Ticket { get; }

    public NDeskConsentRequest ConsentRequest { get; }

    public NDeskSessionState State
    {
        get { lock (_lock) return _state; }
    }

    public event Action<NDeskSessionState>? StateChanged;

    public Task RespondConsentAsync(bool accepted, CancellationToken ct = default)
    {
        NDeskSessionState next;
        lock (_lock)
        {
            if (_state != NDeskSessionState.AwaitingConsent)
                return Task.CompletedTask;

            next = accepted ? NDeskSessionState.Connected : NDeskSessionState.Ended;
            _state = next;
        }

        StateChanged?.Invoke(next);
        return Task.CompletedTask;
    }

    public Task EndAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_state == NDeskSessionState.Ended)
                return Task.CompletedTask;

            _state = NDeskSessionState.Ended;
        }

        StateChanged?.Invoke(NDeskSessionState.Ended);
        return Task.CompletedTask;
    }
}
