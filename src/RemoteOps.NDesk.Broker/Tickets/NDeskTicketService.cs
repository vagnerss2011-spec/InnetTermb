using Microsoft.EntityFrameworkCore;
using RemoteOps.Contracts.NDesk;
using RemoteOps.NDesk.Broker;
using RemoteOps.NDesk.Broker.Audit;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Data.Entities;
using RemoteOps.NDesk.Broker.Security;

namespace RemoteOps.NDesk.Broker.Tickets;

/// <summary>
/// Emissão e ciclo de vida do ticket NDesk (contracts/ndesk-ticket.schema.json).
/// Regras inegociáveis: TTL curto, single-use, link token aleatório criptográfico,
/// e o valor cru do token nunca é persistido nem logado (só existe no retorno da emissão).
/// </summary>
public sealed class NDeskTicketService(
    NDeskDbContext db,
    NDeskAuditService audit,
    TimeProvider clock,
    ILogger<NDeskTicketService> logger)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);

    public async Task<NDeskTicket> IssueTicketAsync(IssueTicketRequest req, CancellationToken ct = default)
    {
        var ttl = req.Ttl is { } t && t > TimeSpan.Zero && t <= MaxTtl ? t : DefaultTtl;
        var rawToken = NDeskTokenHasher.GenerateRawToken();
        var now = clock.GetUtcNow();

        var entity = new NDeskTicketEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = req.WorkspaceId,
            CreatedByUserId = req.CreatedByUserId,
            LinkTokenHash = NDeskTokenHasher.Hash(rawToken),
            ExpiresAt = now.Add(ttl),
            Status = "waiting",
            PermissionsRequested = NDeskEnums.ToCsv(req.PermissionsRequested ?? []),
            RequestedMode = req.RequestedMode,
            AgentMinimumWindows = req.AgentMinimumWindows,
            AgentAllowWindows7Legacy = req.AgentAllowWindows7Legacy,
            AgentRequiresInstall = req.AgentRequiresInstall,
            CreatedAt = now,
        };

        db.Tickets.Add(entity);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new NDeskAuditRecord(
            Action: "ndesk.ticket.created",
            WorkspaceId: req.WorkspaceId,
            TicketId: entity.Id,
            ActorUserId: req.CreatedByUserId,
            Metadata: new Dictionary<string, object?>
            {
                ["requestedMode"] = req.RequestedMode,
                ["ttlSeconds"] = ttl.TotalSeconds,
            }), ct);

        // rawToken só é devolvido aqui, nesta resposta — nunca persistido em claro, nunca logado.
        return ToContract(entity, linkToken: rawToken);
    }

    public async Task<NDeskTicket?> GetStatusAsync(Guid ticketId, CancellationToken ct = default)
    {
        var entity = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (entity is null) return null;
        await ExpireIfDueAsync(entity, ct);
        return ToContract(entity);
    }

    public async Task<RedeemResult> RedeemTicketAsync(string rawLinkToken, CancellationToken ct = default)
    {
        var hash = NDeskTokenHasher.Hash(rawLinkToken);
        var entity = await db.Tickets.FirstOrDefaultAsync(t => t.LinkTokenHash == hash, ct);
        if (entity is null)
            return new RedeemResult(RedeemOutcome.NotFound, null, null);

        await ExpireIfDueAsync(entity, ct);

        if (entity.Status == "expired")
            return new RedeemResult(RedeemOutcome.Expired, null, ToContract(entity));

        if (entity.Status != "waiting")
            return new RedeemResult(RedeemOutcome.AlreadyUsed, null, ToContract(entity));

        var sessionId = Guid.NewGuid();
        entity.Status = "connected";
        entity.SessionId = sessionId;
        entity.RedeemedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new NDeskAuditRecord(
            Action: "ndesk.ticket.redeemed",
            WorkspaceId: entity.WorkspaceId,
            TicketId: entity.Id,
            SessionId: sessionId), ct);

        logger.LogInformation("NDesk ticket {TicketId} redeemed as session {SessionId}", entity.Id, sessionId);

        return new RedeemResult(RedeemOutcome.Success, sessionId, ToContract(entity));
    }

    /// <summary>Encerra o ticket associado à sessão (closed | denied). Idempotente.</summary>
    public async Task<bool> CloseSessionAsync(Guid sessionId, string finalStatus, CancellationToken ct = default)
    {
        var entity = await db.Tickets.FirstOrDefaultAsync(t => t.SessionId == sessionId, ct);
        if (entity is null || entity.Status is "closed" or "denied") return false;

        entity.Status = finalStatus;
        entity.ClosedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Ticket ativo (status connected) para a sessão — usado pelo gate de signaling.</summary>
    public async Task<NDeskTicketEntity?> FindActiveBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var entity = await db.Tickets.FirstOrDefaultAsync(t => t.SessionId == sessionId, ct);
        return entity is { Status: "connected" } ? entity : null;
    }

    private async Task ExpireIfDueAsync(NDeskTicketEntity entity, CancellationToken ct)
    {
        if (entity.Status == "waiting" && entity.ExpiresAt <= clock.GetUtcNow())
        {
            entity.Status = "expired";
            await db.SaveChangesAsync(ct);
        }
    }

    private static NDeskTicket ToContract(NDeskTicketEntity e, string? linkToken = null) => new()
    {
        Id = e.Id.ToString(),
        WorkspaceId = e.WorkspaceId.ToString(),
        CreatedBy = e.CreatedByUserId?.ToString(),
        ExpiresAt = e.ExpiresAt,
        Status = e.Status,
        PermissionsRequested = NDeskEnums.ParseList(e.PermissionsRequested),
        RequestedMode = e.RequestedMode,
        LinkToken = linkToken,
        AgentCompatibility = new NDeskAgentCompatibility
        {
            MinimumWindows = e.AgentMinimumWindows,
            AllowWindows7Legacy = e.AgentAllowWindows7Legacy,
            RequiresInstall = e.AgentRequiresInstall,
        },
    };
}
