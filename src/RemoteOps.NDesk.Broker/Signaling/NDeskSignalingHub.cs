using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using RemoteOps.NDesk.Broker.Consent;
using RemoteOps.NDesk.Broker.Tickets;

namespace RemoteOps.NDesk.Broker.Signaling;

/// <summary>
/// Hub de rendezvous/signaling (docs/09 §Broker/Signaling, ADR-018). Troca apenas envelopes
/// opacos de SDP/ICE entre operador e agente — o broker NUNCA repassa mídia, nunca inspeciona
/// o conteúdo do payload. Nenhum <see cref="SendSignal"/> é liberado sem consentimento válido
/// (<see cref="NDeskPermissionGrantService.IsSessionAuthorizedAsync"/>).
/// </summary>
public sealed class NDeskSignalingHub(
    NDeskTicketService tickets,
    NDeskPermissionGrantService grants,
    ILogger<NDeskSignalingHub> logger) : Hub
{
    /// <summary>Operador ou agente entram no grupo de signaling da sessão. role: "operator" | "agent".</summary>
    public async Task JoinSession(string sessionId, string role)
    {
        if (!Guid.TryParse(sessionId, out var sid))
            throw new HubException("sessionId inválido.");

        var ticket = await tickets.FindActiveBySessionAsync(sid);
        if (ticket is null)
            throw new HubException("Sessão não encontrada ou já encerrada.");

        if (role == "operator")
        {
            var userIdStr = Context.User?.FindFirstValue("sub");
            if (Context.User?.Identity?.IsAuthenticated != true
                || !Guid.TryParse(userIdStr, out var userId)
                || ticket.CreatedByUserId != userId)
            {
                throw new HubException("Operador não autorizado para esta sessão.");
            }
        }
        else if (role != "agent")
        {
            throw new HubException("role deve ser 'operator' ou 'agent'.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        logger.LogInformation("NDesk signaling: {Role} entrou na sessão {SessionId}", role, sessionId);
    }

    /// <summary>Repassa um envelope opaco de signaling (SDP offer/answer, candidato ICE) ao outro lado.</summary>
    public async Task SendSignal(string sessionId, string type, string payload)
    {
        if (!Guid.TryParse(sessionId, out var sid))
            throw new HubException("sessionId inválido.");

        if (!await grants.IsSessionAuthorizedAsync(sid))
            throw new HubException("Sessão sem consentimento válido — sinal recusado.");

        await Clients.OthersInGroup(sessionId).SendAsync("Signal", type, payload);
    }

    public async Task EndSession(string sessionId, string? reason)
    {
        if (!Guid.TryParse(sessionId, out var sid)) return;

        var actor = Context.User?.Identity?.IsAuthenticated == true
            ? Context.User.FindFirstValue("sub") ?? "operator"
            : "agent";

        await grants.RevokeConsentAsync(sid, actor);
        await Clients.Group(sessionId).SendAsync("SessionEnded", reason);
        logger.LogInformation("NDesk signaling: sessão {SessionId} encerrada por {Actor}", sessionId, actor);
    }
}
