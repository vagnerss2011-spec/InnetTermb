using Microsoft.EntityFrameworkCore;
using RemoteOps.Contracts.NDesk;
using RemoteOps.NDesk.Broker;
using RemoteOps.NDesk.Broker.Audit;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Data.Entities;
using RemoteOps.NDesk.Broker.Tickets;

namespace RemoteOps.NDesk.Broker.Consent;

/// <summary>
/// Consentimento (contracts/ndesk-permission-grant.schema.json). Implementa a regra
/// inegociável do CLAUDE.md/docs/09: nenhuma sessão de signaling é liberada sem um grant
/// explícito, não-revogado e não-expirado — <see cref="IsSessionAuthorizedAsync"/> é o gate
/// consultado pelo Hub antes de repassar qualquer SDP/ICE.
/// </summary>
public sealed class NDeskPermissionGrantService(
    NDeskDbContext db,
    NDeskTicketService tickets,
    NDeskAuditService audit,
    TimeProvider clock,
    ILogger<NDeskPermissionGrantService> logger)
{
    public async Task<GrantConsentResult> GrantConsentAsync(GrantConsentRequest req, CancellationToken ct = default)
    {
        var ticket = await tickets.FindActiveBySessionAsync(req.SessionId, ct);
        if (ticket is null)
            return new GrantConsentResult(GrantOutcome.NoActiveTicket, null);

        // Defesa em profundidade: o consentimento nunca pode conceder mais do que foi solicitado no convite.
        var requestedPermissions = NDeskEnums.ParseList(ticket.PermissionsRequested).ToHashSet();
        if (ticket.RequestedMode is not null && req.Mode != ticket.RequestedMode)
            return new GrantConsentResult(GrantOutcome.PermissionsExceedRequest, null);
        if (req.Permissions.Exists(p => !requestedPermissions.Contains(p)))
            return new GrantConsentResult(GrantOutcome.PermissionsExceedRequest, null);

        var now = clock.GetUtcNow();
        var entity = await db.PermissionGrants.FirstOrDefaultAsync(g => g.SessionId == req.SessionId, ct);
        if (entity is null)
        {
            entity = new NDeskPermissionGrantEntity
            {
                SessionId = req.SessionId,
                TicketId = ticket.Id,
                GrantedByDisplayName = req.GrantedBy.DisplayName,
                GrantedByMachineName = req.GrantedBy.MachineName,
                Mode = req.Mode,
            };
            db.PermissionGrants.Add(entity);
        }

        entity.GrantedByDisplayName = req.GrantedBy.DisplayName;
        entity.GrantedByWindowsUser = req.GrantedBy.WindowsUser;
        entity.GrantedByMachineName = req.GrantedBy.MachineName;
        entity.Mode = req.Mode;
        entity.Permissions = NDeskEnums.ToCsv(req.Permissions);
        entity.ConsentTextVersion = req.ConsentTextVersion;
        entity.GrantedAt = now;
        entity.ExpiresAt = req.Ttl is { } t && t > TimeSpan.Zero ? now.Add(t) : null;
        entity.RevokedAt = null;
        entity.RevokedBy = null;

        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new NDeskAuditRecord(
            Action: "ndesk.consent.granted",
            WorkspaceId: ticket.WorkspaceId,
            TicketId: ticket.Id,
            SessionId: req.SessionId,
            ActorDisplayName: req.GrantedBy.DisplayName,
            Metadata: new Dictionary<string, object?>
            {
                ["mode"] = req.Mode,
                ["permissions"] = req.Permissions,
                ["machineName"] = req.GrantedBy.MachineName,
            }), ct);

        logger.LogInformation("NDesk consent granted for session {SessionId} mode {Mode}", req.SessionId, req.Mode);

        return new GrantConsentResult(GrantOutcome.Granted, ToContract(entity));
    }

    public async Task DenyConsentAsync(Guid sessionId, string? reason, CancellationToken ct = default)
    {
        var ticket = await tickets.FindActiveBySessionAsync(sessionId, ct);
        await tickets.CloseSessionAsync(sessionId, "denied", ct);

        await audit.RecordAsync(new NDeskAuditRecord(
            Action: "ndesk.consent.denied",
            WorkspaceId: ticket?.WorkspaceId,
            TicketId: ticket?.Id,
            SessionId: sessionId,
            Metadata: reason is null ? null : new Dictionary<string, object?> { ["reason"] = reason }), ct);
    }

    public async Task RevokeConsentAsync(Guid sessionId, string revokedBy, CancellationToken ct = default)
    {
        var entity = await db.PermissionGrants.FirstOrDefaultAsync(g => g.SessionId == sessionId, ct);
        if (entity is not null && entity.RevokedAt is null)
        {
            entity.RevokedAt = clock.GetUtcNow();
            entity.RevokedBy = revokedBy;
            await db.SaveChangesAsync(ct);
        }

        await tickets.CloseSessionAsync(sessionId, "closed", ct);

        await audit.RecordAsync(new NDeskAuditRecord(
            Action: "ndesk.consent.revoked",
            SessionId: sessionId,
            Metadata: new Dictionary<string, object?> { ["revokedBy"] = revokedBy }), ct);
    }

    /// <summary>Gate central: nenhuma troca de sinal (SDP/ICE) é liberada sem isto retornar true.</summary>
    public async Task<bool> IsSessionAuthorizedAsync(Guid sessionId, CancellationToken ct = default)
    {
        var grant = await db.PermissionGrants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.SessionId == sessionId, ct);
        if (grant is null || grant.RevokedAt is not null) return false;
        if (grant.ExpiresAt is { } exp && exp <= clock.GetUtcNow()) return false;
        return true;
    }

    private static NDeskPermissionGrant ToContract(NDeskPermissionGrantEntity e) => new()
    {
        SessionId = e.SessionId.ToString(),
        TicketId = e.TicketId.ToString(),
        GrantedBy = new NDeskGrantedBy
        {
            DisplayName = e.GrantedByDisplayName,
            WindowsUser = e.GrantedByWindowsUser,
            MachineName = e.GrantedByMachineName,
        },
        GrantedAt = e.GrantedAt,
        Mode = e.Mode,
        Permissions = NDeskEnums.ParseList(e.Permissions),
        ExpiresAt = e.ExpiresAt,
        RevokedAt = e.RevokedAt,
        RevokedBy = e.RevokedBy,
        ConsentTextVersion = e.ConsentTextVersion,
    };
}
