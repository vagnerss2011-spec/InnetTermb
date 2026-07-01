using System.Collections.Concurrent;
using RemoteOps.Contracts.NDesk;

namespace RemoteOps.Desktop.NDesk;

/// <summary>
/// Broker fake in-memory (loopback, sem rede) — registro de tickets em processo único.
/// Único registro DI hoje; a troca pelo broker real (Frente 3) é uma troca de binding.
/// </summary>
public sealed class LoopbackNDeskBrokerClient : INDeskBrokerClient
{
    private const string CompanyName = "RemoteOps";

    private readonly ConcurrentDictionary<string, NDeskTicket> _tickets = new();

    public event Action<INDeskAgentSession>? IncomingSessionRequested;

    public Task<NDeskTicket> CreateTicketAsync(
        string workspaceId,
        string operatorDisplayName,
        string requestedMode,
        IReadOnlyList<string> permissionsRequested,
        CancellationToken ct = default)
    {
        var ticket = new NDeskTicket
        {
            Id = Random.Shared.Next(0, 1_000_000).ToString("D6"),
            WorkspaceId = workspaceId,
            CreatedBy = operatorDisplayName,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            Status = "waiting",
            RequestedMode = requestedMode,
            PermissionsRequested = permissionsRequested.ToList(),
        };

        _tickets[ticket.Id] = ticket;
        return Task.FromResult(ticket);
    }

    public Task<INDeskAgentSession> ConnectAsync(string ticketId, CancellationToken ct = default)
    {
        if (!_tickets.TryGetValue(ticketId, out var ticket))
            throw new InvalidOperationException($"Ticket '{ticketId}' não encontrado.");

        var consentRequest = new NDeskConsentRequest(
            ticket.Id,
            ticket.CreatedBy ?? "Operador",
            CompanyName,
            ticket.RequestedMode ?? "control",
            ticket.PermissionsRequested);

        INDeskAgentSession session = new LoopbackNDeskAgentSession(ticket, consentRequest);
        IncomingSessionRequested?.Invoke(session);
        return Task.FromResult(session);
    }
}
