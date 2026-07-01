using RemoteOps.Contracts.NDesk;

namespace RemoteOps.Desktop.NDesk;

/// <summary>
/// Seam do broker NDesk. Hoje só existe uma implementação fake in-memory (sem rede) —
/// a implementação real (Frente 3) entra depois via troca de registro DI.
/// </summary>
public interface INDeskBrokerClient
{
    Task<NDeskTicket> CreateTicketAsync(
        string workspaceId,
        string operatorDisplayName,
        string requestedMode,
        IReadOnlyList<string> permissionsRequested,
        CancellationToken ct = default);

    Task<INDeskAgentSession> ConnectAsync(string ticketId, CancellationToken ct = default);

    /// <summary>Disparado quando um operador conecta a um ticket — o lado atendido escuta isto.</summary>
    event Action<INDeskAgentSession>? IncomingSessionRequested;
}
