using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Teams;

/// <summary>Desfecho da remoção de membro — cada um vira um status diferente no endpoint.</summary>
public enum RemoveMemberOutcome
{
    /// <summary>Membership apagada: o acesso está cortado do próximo request em diante.</summary>
    Removed,

    /// <summary>Não havia membership — nada a fazer (a tela não pode dizer que removeu).</summary>
    NotAMember,

    /// <summary>Era o último dono: remover deixaria o workspace sem ninguém que possa administrá-lo.</summary>
    LastOwner,
}

/// <summary>
/// Membros do time: quem está dentro, tirar alguém de dentro e entregar a cada um o SEU embrulho da
/// chave do workspace.
///
/// <para><b>A verdade sobre remover:</b> apagar a membership corta o acesso DAQUI PRA FRENTE — o
/// <see cref="PermissionEvaluator"/> checa membership em cada requisição, então o próximo pull/push
/// do ex-membro é negado. O que ele já baixou continua na máquina dele, e não existe comando que
/// desfaça isso. Fingir o contrário seria mentir na tela; a resposta completa é operacional (trocar
/// as senhas nos equipamentos), e é isso que a UI da Fatia 1e diz em voz alta.</para>
/// </summary>
public sealed class TeamService(
    AppDbContext db,
    PermissionEvaluator rbac,
    AuditService audit,
    ILogger<TeamService> logger)
{
    // ── GET /workspaces/{id}/members ──────────────────────────────────────────

    /// <summary>
    /// Lista os membros. Exige <see cref="Permissions.SyncPull"/> — quem pode baixar o cofre tem o
    /// direito (e a necessidade de segurança) de saber com quem ele é compartilhado.
    /// </summary>
    public async Task<TeamMembersResponse> ListMembersAsync(
        Guid workspaceId, PermissionContext permCtx, CancellationToken ct)
    {
        await RequireAsync(permCtx, Permissions.SyncPull, workspaceId, "member list", ct);

        var rows = await db.Memberships.AsNoTracking()
            .Include(m => m.User)
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.User.Email)
            .ToListAsync(ct);

        // Sem blob na lista: o embrulho de um membro só abre com a AMK DELE, então mandá-lo para os
        // outros não serviria a ninguém e só aumentaria a superfície.
        return new TeamMembersResponse(rows.Select(m => new TeamMember(
            m.UserId.ToString(),
            m.User.Email,
            m.User.DisplayName,
            m.Role,
            HasWk: m.WrappedWk is not null,
            m.WkVersion)).ToList());
    }

    // ── DELETE /workspaces/{id}/members/{userId} ──────────────────────────────

    /// <summary>
    /// Tira o membro do time. Exige <see cref="Permissions.UserDisable"/> (Owner/Admin) — um Manager
    /// convida, mas não expulsa.
    /// </summary>
    public async Task<RemoveMemberOutcome> RemoveMemberAsync(
        Guid workspaceId, Guid targetUserId, PermissionContext permCtx, CancellationToken ct)
    {
        await RequireAsync(permCtx, Permissions.UserDisable, workspaceId, "member removal", ct);

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetUserId, ct);
        if (membership is null)
        {
            logger.LogInformation(
                "Member removal no-op: user {TargetUserId} is not a member of workspace {WorkspaceId}",
                targetUserId, workspaceId);
            return RemoveMemberOutcome.NotAMember;
        }

        // Último dono: sem ele o workspace fica sem ninguém que possa convidar, remover ou mudar
        // papéis — e não há tela que conserte isso depois. Falhar aqui é o único jeito honesto.
        if (membership.Role == Roles.Owner)
        {
            var owners = await db.Memberships.AsNoTracking()
                .CountAsync(m => m.WorkspaceId == workspaceId && m.Role == Roles.Owner, ct);
            if (owners <= 1)
            {
                logger.LogWarning(
                    "Member removal refused: user {TargetUserId} is the last owner of workspace {WorkspaceId}",
                    targetUserId, workspaceId);
                return RemoveMemberOutcome.LastOwner;
            }
        }

        // Convites pendentes para essa pessoa morrem junto: deixá-los vivos seria uma porta de volta
        // com o mesmo código que ela já conhece.
        var email = await db.Users.AsNoTracking()
            .Where(u => u.Id == targetUserId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (email is not null)
        {
            var pending = await db.Invites
                .Where(i => i.WorkspaceId == workspaceId && i.Email == email && i.AcceptedAt == null)
                .ToListAsync(ct);
            db.Invites.RemoveRange(pending);
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: workspaceId,
            ActorUserId: permCtx.UserId,
            Action: "member.removed",
            TargetType: "Membership",
            TargetId: targetUserId,
            DeviceId: permCtx.DeviceId,
            Metadata: new Dictionary<string, object?>
            {
                ["role"] = membership.Role,
                // Registro do que o corte NÃO faz — a auditoria precisa mostrar que a rotação de
                // senhas nos equipamentos ficou pendente do lado humano.
                ["revokesFutureAccessOnly"] = true,
            }), ct);

        logger.LogInformation(
            "Member {TargetUserId} removed from workspace {WorkspaceId} by user {UserId} (role {Role})",
            targetUserId, workspaceId, permCtx.UserId, membership.Role);

        return RemoveMemberOutcome.Removed;
    }

    // ── GET /workspaces/{id}/key ──────────────────────────────────────────────

    /// <summary>
    /// A WK do time embrulhada sob a AMK de QUEM PERGUNTA (nunca a de outro membro). É o que faz o
    /// SEGUNDO device do membro abrir o cofre: a AMK é portável entre devices, mas o embrulho fica
    /// no disco de quem aceitou o convite. Sem isto o colega loga no PC de casa, sincroniza e não
    /// abre nada — falha muda, exatamente o que não pode acontecer.
    ///
    /// <para><c>null</c> = a membership não tem WK (cofre pessoal, que deriva a chave da AMK em vez
    /// de guardá-la).</para>
    /// </summary>
    public async Task<WorkspaceKeyResponse?> GetWorkspaceKeyAsync(
        Guid workspaceId, PermissionContext permCtx, CancellationToken ct)
    {
        await RequireAsync(permCtx, Permissions.SyncPull, workspaceId, "workspace key", ct);

        var membership = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == permCtx.UserId, ct);
        if (membership?.WrappedWk is null)
            return null;

        return new WorkspaceKeyResponse(
            workspaceId.ToString(), Convert.ToBase64String(membership.WrappedWk), membership.WkVersion);
    }

    private async Task RequireAsync(
        PermissionContext permCtx, string permission, Guid workspaceId, string what, CancellationToken ct)
    {
        var check = await rbac.EvaluateAsync(permCtx, permission, ct);
        if (check.Granted) return;

        logger.LogWarning("Team {What} denied for user {UserId} workspace {WorkspaceId}: {Reason}",
            what, permCtx.UserId, workspaceId, check.Reason);
        throw new RbacDeniedException(check.Reason);
    }
}
