using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Auth;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Email;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Teams;

/// <summary>
/// Convite para o workspace de TIME (Fatia 1). Mesma disciplina do
/// <see cref="PasswordResetService"/> — hash, TTL, uso único e resposta ANTI-ENUMERAÇÃO — mais a
/// peça que o compartilhamento exige: a chave do time viaja EMBRULHADA e o servidor não tem como
/// abri-la.
///
/// <para><b>A fronteira E2EE aqui:</b> o cliente do dono sorteia o código de 160 bits, deriva
/// <c>K_invite = HKDF(código)</c>, embrulha a WK e sobe o BLOB + o SHA-256 do código. Este serviço
/// guarda os dois e compara hashes. O código nunca chega, nunca é logado e — principalmente — nunca
/// vai no e-mail: e-mail e código viajam por canais diferentes de propósito, senão quem lê a caixa
/// de entrada do convidado entra no cofre do time sozinho.</para>
///
/// <para><b>Por que toda recusa devolve a MESMA coisa (null):</b> convite inexistente, expirado, já
/// usado, de outro e-mail ou com código errado são indistinguíveis para quem chama. Diferenciá-los
/// transformaria o endpoint num oráculo de "esse e-mail tem convite pendente para esse time" — e o
/// motivo real fica no log do servidor, onde serve ao operador e não ao atacante.</para>
/// </summary>
public sealed class InviteService(
    AppDbContext db,
    PermissionEvaluator rbac,
    AuditService audit,
    IEmailSender email,
    IConfiguration config,
    ILogger<InviteService> logger)
{
    /// <summary>
    /// TTL do convite. Bem maior que o do reset de senha (30 min) porque o fluxo é humano e
    /// assíncrono: o e-mail chega, o código vem por WhatsApp/telefone e o colega instala o app.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>SHA-256 em hex: 64 caracteres, sempre.</summary>
    private const int CodeHashLength = 64;

    /// <summary>Seam de teste do relógio (expiração). Por padrão é o real.</summary>
    internal Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    // ── POST /workspaces/{id}/invites ─────────────────────────────────────────

    /// <summary>
    /// Cria o convite. Exige <see cref="Permissions.UserInvite"/> no workspace. Tudo que é sensível
    /// já chegou pronto do cliente (hash + blob): aqui só se valida FORMA e se grava.
    /// </summary>
    public async Task<CreateInviteResponse> CreateAsync(
        Guid workspaceId, CreateInviteRequest req, PermissionContext permCtx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var check = await rbac.EvaluateAsync(permCtx, Permissions.UserInvite, ct);
        if (!check.Granted)
        {
            logger.LogWarning(
                "Invite denied for user {UserId} workspace {WorkspaceId}: {Reason}",
                permCtx.UserId, workspaceId, check.Reason);
            throw new RbacDeniedException(check.Reason);
        }

        var inviteeEmail = EmailNormalizer.Normalize(req.Email ?? string.Empty);
        if (string.IsNullOrEmpty(inviteeEmail) || !inviteeEmail.Contains('@'))
            throw new ArgumentException("E-mail do convidado inválido.");

        var role = (req.Role ?? string.Empty).Trim();
        if (!Roles.IsKnown(role))
            throw new ArgumentException("Papel desconhecido.");

        // Escalonamento: quem convida não pode fabricar alguém mais poderoso que ele. Sem isto, um
        // Manager (que tem user.invite) cria um Owner e é removido pela própria criatura.
        var inviter = await db.Memberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == permCtx.UserId, ct);
        if (inviter is null || !Roles.PermissionsOf(role).IsSubsetOf(Roles.PermissionsOf(inviter.Role)))
            throw new ArgumentException("Você não pode convidar alguém com mais poder que o seu papel.");

        var codeHash = NormalizeCodeHash(req.CodeHash);
        var wrapped = WrappedKeyBlob.Decode(req.WrappedWkByInvite, nameof(req.WrappedWkByInvite));

        // Versão da WK desde o dia 1: sem ela, uma rotação futura produz estado misto INDETECTÁVEL.
        if (req.WkVersion < 1)
            throw new ArgumentException("wkVersion deve ser >= 1.");

        // Já é do time? O dono está autenticado e autorizado NESTE workspace — dizer a verdade para
        // ele não é enumeração, é a informação que evita um convite que nunca funcionaria.
        var alreadyMember = await db.Users.AsNoTracking()
            .Where(u => u.Email == inviteeEmail)
            .Join(db.Memberships.AsNoTracking().Where(m => m.WorkspaceId == workspaceId),
                u => u.Id, m => m.UserId, (u, m) => m.UserId)
            .AnyAsync(ct);
        if (alreadyMember)
            throw new InviteConflictException("Essa pessoa já é membro deste time.");

        var now = UtcNow();

        // Só o convite mais novo vale (mesma disciplina do reset de senha): um convite anterior
        // ainda pendente para o mesmo e-mail deixa de servir, então um código vazado envelhece.
        var superseded = await db.Invites
            .Where(i => i.WorkspaceId == workspaceId && i.Email == inviteeEmail && i.AcceptedAt == null)
            .ToListAsync(ct);
        db.Invites.RemoveRange(superseded);

        var invite = new InviteEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Email = inviteeEmail,
            Role = role,
            CodeHash = codeHash,
            WrappedWkByInvite = wrapped,
            WkVersion = req.WkVersion,
            ExpiresAt = now.Add(Lifetime),
            InvitedByUserId = permCtx.UserId,
            CreatedAt = now,
        };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);

        var workspaceName = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct) ?? "RemoteOps";

        // O e-mail é CONVENIÊNCIA, não o convite. Se o SMTP estiver fora do ar, o convite continua
        // válido e o dono repassa link e código na mão — mas ele precisa SABER disso (EmailDelivered),
        // senão fica esperando um e-mail que nunca vai chegar. Falha silenciosa é o defeito
        // estrutural desta base; aqui ela avisa em dois lugares: no log e na resposta.
        var delivered = true;
        try
        {
            await email.SendAsync(BuildInviteEmail(invite, workspaceName), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            delivered = false;
            logger.LogError(
                ex,
                "Invite {InviteId} gravado, mas o email NÃO saiu (workspace {WorkspaceId}). "
                + "Quem convidou precisa repassar o link e o código na mão.",
                invite.Id, workspaceId);
        }

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: workspaceId,
            ActorUserId: permCtx.UserId,
            Action: "invite.created",
            TargetType: "Invite",
            TargetId: invite.Id,
            DeviceId: permCtx.DeviceId,
            // Só metadado. Nada aqui reconstrói o código nem o blob (ADR-013).
            Metadata: new Dictionary<string, object?>
            {
                ["role"] = role,
                ["expiresAt"] = invite.ExpiresAt,
                ["emailDelivered"] = delivered,
                ["superseded"] = superseded.Count,
            }), ct);

        logger.LogInformation(
            "Invite {InviteId} created in workspace {WorkspaceId} by user {UserId} "
            + "(role {Role}, email delivered: {Delivered}, superseded: {Superseded})",
            invite.Id, workspaceId, permCtx.UserId, role, delivered, superseded.Count);

        return new CreateInviteResponse(
            invite.Id.ToString(), invite.Email, invite.Role, invite.ExpiresAt, delivered);
    }

    // ── POST /invites/{id}/context ────────────────────────────────────────────

    /// <summary>
    /// Troca a prova do código pelo blob da WK. NÃO consome o convite (o aceite ainda vai usá-lo) —
    /// mesma separação do <c>password/reset-context</c>. <c>null</c> = recusa genérica.
    /// </summary>
    public async Task<InviteContextResponse?> GetContextAsync(
        Guid inviteId, string codeHash, Guid callerUserId, CancellationToken ct)
    {
        var invite = await ResolveAsync(inviteId, codeHash, callerUserId, ct);
        if (invite is null) return null;

        return new InviteContextResponse(
            invite.WorkspaceId.ToString(),
            invite.Workspace.Name,
            invite.Role,
            Convert.ToBase64String(invite.WrappedWkByInvite),
            invite.WkVersion);
    }

    // ── POST /invites/{id}/accept ─────────────────────────────────────────────

    /// <summary>
    /// Conclui o aceite: cria a membership com a WK re-embrulhada pelo convidado e queima o convite
    /// — na MESMA gravação. <c>null</c> = a mesma recusa genérica do contexto.
    ///
    /// <para>A atomicidade não é enfeite: com duas requisições concorrentes, marcar o convite depois
    /// de criar a membership (ou vice-versa, em gravações separadas) deixa a porta aberta para dois
    /// aceites. Aqui, o <c>AcceptedAt</c> é token de concorrência — a UPDATE só passa se o convite
    /// AINDA estiver pendente — e a chave primária da membership (workspace+usuário) é o segundo
    /// muro. Quem perde a corrida cai no mesmo <c>null</c> de sempre.</para>
    /// </summary>
    public async Task<AcceptInviteResponse?> AcceptAsync(
        Guid inviteId, AcceptInviteRequest req, Guid callerUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var invite = await ResolveAsync(inviteId, req.CodeHash, callerUserId, ct);
        if (invite is null) return null;

        // Material inválido LANÇA (400): é erro do cliente, não recusa de convite, e gravar uma
        // membership com blob quebrado deixaria o colega dentro do time sem conseguir abrir nada.
        var wrappedWk = WrappedKeyBlob.Decode(req.WrappedWk, nameof(req.WrappedWk));

        var now = UtcNow();
        invite.AcceptedAt = now;
        invite.AcceptedByUserId = callerUserId;

        db.Memberships.Add(new MembershipEntity
        {
            WorkspaceId = invite.WorkspaceId,
            UserId = callerUserId,
            Role = invite.Role,
            WrappedWk = wrappedWk,
            // A versão vem do CONVITE, nunca do corpo: senão o cliente declara a versão que quiser e
            // uma rotação futura acha que aquele membro já está na chave nova.
            WkVersion = invite.WkVersion,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            // Perdeu a corrida: ou o token do AcceptedAt barrou a UPDATE (convite já queimado por
            // outra requisição), ou a chave primária da membership barrou a segunda inserção. O
            // provider InMemory sinaliza a colisão de chave como ArgumentException e o relacional
            // como DbUpdateException — os dois são o MESMO fato aqui. Recusa genérica, como todas.
            //
            // A exceção vai INTEIRA para o log de propósito: este catch é largo, e sem o rastro um
            // erro de modelo mal-formado se disfarçaria de "convite inválido" para sempre. Nada aqui
            // carrega segredo — o EF reporta a chave, não o conteúdo.
            logger.LogWarning(
                ex, "Invite {InviteId} refused for user {UserId}: concurrent-accept",
                inviteId, callerUserId);
            return null;
        }

        // O token que o convidado tem na mão ficou obsoleto? Ver AcceptInviteResponse: entrar num
        // time de OUTRO tenant invalida na prática o claim `tenant_id` já emitido, e sem avisar o
        // cofre do time responderia 403 "sem motivo" até o token expirar.
        var tenants = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == callerUserId && m.Workspace.Status == "active")
            .Select(m => m.Workspace.TenantId)
            .Distinct()
            .Take(2)
            .CountAsync(ct);
        var sessionRefreshRequired = tenants > 1;

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: invite.WorkspaceId,
            ActorUserId: callerUserId,
            Action: "invite.accepted",
            TargetType: "Invite",
            TargetId: invite.Id,
            Metadata: new Dictionary<string, object?>
            {
                ["role"] = invite.Role,
                ["wkVersion"] = invite.WkVersion,
                ["invitedBy"] = invite.InvitedByUserId,
            }), ct);

        logger.LogInformation(
            "Invite {InviteId} accepted by user {UserId} in workspace {WorkspaceId} "
            + "(role {Role}, session refresh required: {Refresh})",
            invite.Id, callerUserId, invite.WorkspaceId, invite.Role, sessionRefreshRequired);

        return new AcceptInviteResponse(
            invite.WorkspaceId.ToString(),
            invite.Workspace.Name,
            invite.Role,
            invite.WkVersion,
            sessionRefreshRequired);
    }

    // ── Interno ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Convite válido PARA ESTE chamador, ou <c>null</c>. Todas as recusas saem por aqui, com a
    /// mesma cara para quem chama e o motivo só no log.
    /// </summary>
    private async Task<InviteEntity?> ResolveAsync(
        Guid inviteId, string? codeHash, Guid callerUserId, CancellationToken ct)
    {
        // Formato errado (inclusive o caso de um cliente mandar o CÓDIGO cru aqui) recusa igual, sem
        // ecoar o valor em lugar nenhum.
        if (!TryNormalizeCodeHash(codeHash, out var provided))
            return Refuse(inviteId, callerUserId, "code.malformed");

        var caller = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == callerUserId, ct);
        if (caller is null || caller.Status != "active")
            return Refuse(inviteId, callerUserId, "caller.inactive");

        // Busca pelo ID do convite (que veio no link), não pelo e-mail: assim não existe consulta
        // "tem convite para fulano?" para começo de conversa.
        var invite = await db.Invites.Include(i => i.Workspace)
            .FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null)
            return Refuse(inviteId, callerUserId, "invite.not-found");

        // Workspace desativado não recebe gente nova. O RBAC do resto do sistema já recusa
        // workspace inativo; sem esta linha o convite seria a única porta que continuaria aberta.
        if (invite.Workspace.Status != "active")
            return Refuse(inviteId, callerUserId, "workspace.inactive");

        if (invite.AcceptedAt is not null)
            return Refuse(inviteId, callerUserId, "invite.used");

        if (invite.ExpiresAt <= UtcNow())
            return Refuse(inviteId, callerUserId, "invite.expired");

        // O convite é para UM e-mail. Sem esta trava, quem descobrisse o código (ele viaja por
        // WhatsApp) entraria no cofre do time com qualquer conta.
        if (!string.Equals(invite.Email, caller.Email, StringComparison.Ordinal))
            return Refuse(inviteId, callerUserId, "invite.email-mismatch");

        // Tempo constante: o hash é o que separa quem tem o código de quem está chutando; comparar
        // com == vazaria, byte a byte, o quanto o chute chegou perto.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(invite.CodeHash), Encoding.ASCII.GetBytes(provided)))
        {
            return Refuse(inviteId, callerUserId, "invite.code-mismatch");
        }

        return invite;
    }

    private InviteEntity? Refuse(Guid inviteId, Guid callerUserId, string reason)
    {
        // O motivo fica AQUI, não na resposta: o operador precisa dele para socorrer o colega, o
        // atacante não pode tê-lo. Só id/GUID — nada de código, hash, blob ou e-mail (ADR-013).
        logger.LogWarning("Invite {InviteId} refused for user {UserId}: {Reason}",
            inviteId, callerUserId, reason);
        return null;
    }

    private static string NormalizeCodeHash(string? value) =>
        TryNormalizeCodeHash(value, out var hash)
            ? hash
            : throw new ArgumentException(
                "codeHash precisa ser o SHA-256 do código em hex (64 caracteres). "
                + "O CÓDIGO em si nunca deve ser enviado ao servidor.");

    /// <summary>
    /// Aceita só 64 hex. É guarda de FORMATO com peso de segurança: um cliente que mandasse o código
    /// cru no lugar do hash entregaria a chave do time ao servidor e mataria o E2EE em silêncio —
    /// o código (base32 com hífens, do <c>RecoveryKeyCodec</c>) não passa por aqui.
    /// </summary>
    private static bool TryNormalizeCodeHash(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null) return false;

        var trimmed = value.Trim();
        if (trimmed.Length != CodeHashLength) return false;

        foreach (var c in trimmed)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }

        normalized = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// O e-mail do convite. <b>NUNCA</b> leva o código — e não é só disciplina: o enviador default
    /// (<c>LoggingEmailSender</c>) despeja o corpo inteiro no log quando não há SMTP, então um código
    /// aqui viraria código no log do servidor. Sem contar o óbvio: e-mail e código no mesmo canal
    /// fazem de qualquer caixa de entrada invadida uma porta aberta para o cofre do time.
    /// </summary>
    private EmailMessage BuildInviteEmail(InviteEntity invite, string workspaceName)
    {
        var linkBase = config["Invites:LinkBase"];
        var link = string.IsNullOrWhiteSpace(linkBase)
            ? $"    Identificador do convite: {invite.Id}"
            : $"    {linkBase.TrimEnd('/')}/{invite.Id}";

        return new EmailMessage(
            invite.Email,
            $"RemoteOps — convite para o time {workspaceName}",
            $"Você foi convidado para o time \"{workspaceName}\" no RemoteOps.\n\n"
            + "Convite:\n\n"
            + link + "\n\n"
            + "O CÓDIGO DO CONVITE NÃO ESTÁ NESTE E-MAIL — de propósito. Quem convidou vai te "
            + "passar o código por OUTRO CANAL (WhatsApp, telefone, pessoalmente). São duas "
            + "metades: sem o código, este convite não abre o cofre do time. É isso que protege as "
            + "senhas da equipe caso este e-mail caia em mãos erradas.\n\n"
            + "No RemoteOps: entre na sua conta, abra Equipe, escolha \"Tenho um convite\", informe "
            + "o identificador acima e digite o código que você recebeu pelo outro canal.\n\n"
            + $"O convite expira em {invite.ExpiresAt:dd/MM/yyyy HH:mm} (UTC) e só pode ser usado "
            + "uma vez.\n\n"
            + "Se você não esperava este convite, ignore este e-mail e avise quem administra o time.");
    }
}

/// <summary>
/// Convite que não faz sentido para um pedido AUTORIZADO (ex.: a pessoa já é membro). Diferente da
/// recusa genérica do aceite: aqui quem pergunta é o dono do time, sobre o próprio time — esconder
/// dele só produziria um convite que nunca funcionaria.
/// </summary>
public sealed class InviteConflictException(string message) : Exception(message);
