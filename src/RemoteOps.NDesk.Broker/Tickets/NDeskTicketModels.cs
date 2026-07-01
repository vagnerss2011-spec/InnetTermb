using RemoteOps.Contracts.NDesk;

namespace RemoteOps.NDesk.Broker.Tickets;

public sealed record IssueTicketRequest(
    Guid WorkspaceId,
    Guid? CreatedByUserId,
    string? RequestedMode,
    List<string>? PermissionsRequested,
    TimeSpan? Ttl,
    string? AgentMinimumWindows,
    bool AgentAllowWindows7Legacy,
    bool AgentRequiresInstall);

public enum RedeemOutcome
{
    Success,
    NotFound,
    Expired,
    AlreadyUsed,
}

public sealed record RedeemResult(RedeemOutcome Outcome, Guid? SessionId, NDeskTicket? Ticket);
