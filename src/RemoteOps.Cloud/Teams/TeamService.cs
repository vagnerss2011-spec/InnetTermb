using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Teams;

/// <summary>Desfecho da publicação do embrulho da chave — cada um vira um status diferente.</summary>
public enum PublishWorkspaceKeyOutcome
{
    /// <summary>Não havia embrulho para este membro; agora há.</summary>
    Stored,

    /// <summary>Já havia EXATAMENTE este blob — nada foi escrito (republicação idempotente).</summary>
    AlreadyPublished,

    /// <summary>
    /// Já havia um embrulho DIFERENTE. O servidor não sabe (e não pode saber) se é a mesma chave com
    /// nonce novo ou outra chave; sabe que uma segunda gravação divergente é sinal de bifurcação.
    /// </summary>
    Divergent,
}

/// <summary>Desfecho da criação de um workspace de time — cada um vira um status diferente.</summary>
public enum CreateWorkspaceOutcome
{
    /// <summary>Workspace + membership Owner + embrulho da WK gravados numa transação.</summary>
    Created,

    /// <summary>
    /// Já existe workspace com este id. Recusa SEM dizer de quem ele é: o id vem do cliente, e
    /// responder "é do fulano" transformaria este endpoint num oráculo de enumeração.
    /// </summary>
    IdAlreadyExists,
}

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
    // ── POST /workspaces ──────────────────────────────────────────────────────

    /// <summary>
    /// Cria um workspace de TIME no tenant de quem pede, <b>vazio</b>, com a membership Owner do
    /// criador já carregando o embrulho da WK.
    ///
    /// <para><b>Por que existe:</b> antes disto o único caminho que criava workspace era o
    /// <c>/auth/register</c>, então o "time" era forçosamente o workspace PESSOAL do dono. E o
    /// <c>/sync</c> é escopado por workspace + membership: convidar alguém baixaria o acervo inteiro
    /// do operador (nomes de cliente, IPs, grupos, vendors) no computador do convidado. Não é
    /// vazamento de senha, é de METADADO — e nenhum indicador de cofre fala sobre isso.</para>
    ///
    /// <para><b>Por que atômico:</b> workspace, membership e embrulho entram na MESMA gravação. Um
    /// workspace de time sem chave guardada é um estado que o cliente não consegue classificar (nem
    /// pessoal, nem time), e a lição do 1e′ é exatamente que a custódia do embrulho não pode depender
    /// de um segundo passo que pode falhar.</para>
    ///
    /// <para><b>Sem <see cref="PermissionEvaluator"/>:</b> não há workspace ainda, logo não há
    /// membership para avaliar. A autorização aqui é ser uma conta autenticada — e o tenant vem
    /// SEMPRE de quem chama, nunca do corpo, senão qualquer conta plantaria workspace no tenant
    /// alheio.</para>
    /// </summary>
    public async Task<(CreateWorkspaceOutcome Outcome, CreateWorkspaceResponse? Response)> CreateWorkspaceAsync(
        CreateWorkspaceRequest req, PermissionContext permCtx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!Guid.TryParse(req.Id, out var workspaceId) || workspaceId == Guid.Empty)
            throw new ArgumentException("id deve ser um GUID válido.");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("name é obrigatório.");

        var wrapped = WrappedKeyBlob.Decode(req.WrappedWk, nameof(req.WrappedWk));

        // Versão desde o dia 1, mesma regra do convite e da publicação: zero significa "membership
        // sem chave", e gravar um embrulho carimbado assim tornaria uma rotação futura indetectável.
        if (req.WkVersion < 1)
            throw new ArgumentException("wkVersion deve ser >= 1.");

        // O tenant é o de QUEM PEDE. Determinístico de propósito: o workspace mais ANTIGO em que a
        // conta é Owner é o que nasceu no /auth/register — o tenant próprio dela. Quem já aceitou
        // convite de outro time pertence a dois tenants, e sem esta âncora o time novo poderia cair
        // no tenant do colega, que é de quem ele nunca deveria ter sido.
        var tenantId = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == permCtx.UserId && m.Role == Roles.Owner)
            .OrderBy(m => m.Workspace.CreatedAt)
            .Select(m => m.Workspace.TenantId)
            .FirstOrDefaultAsync(ct);

        if (tenantId == Guid.Empty)
        {
            // Conta sem workspace próprio é modelo inconsistente (o registro sempre cria um). Melhor
            // recusar do que inventar um tenant.
            logger.LogWarning(
                "Workspace creation denied for user {UserId}: no owned workspace to anchor the tenant",
                permCtx.UserId);
            throw new RbacDeniedException("tenant.missing");
        }

        if (await db.Workspaces.AsNoTracking().AnyAsync(w => w.Id == workspaceId, ct))
        {
            // Sem dizer de quem é: o id vem do cliente, e responder "esse é do fulano" faria deste
            // endpoint um oráculo de enumeração de workspaces alheios.
            logger.LogWarning(
                "Workspace creation refused for user {UserId}: id {WorkspaceId} already exists",
                permCtx.UserId, workspaceId);
            return (CreateWorkspaceOutcome.IdAlreadyExists, null);
        }

        var now = DateTimeOffset.UtcNow;
        db.Workspaces.Add(new WorkspaceEntity
        {
            Id = workspaceId,
            TenantId = tenantId,
            Name = req.Name.Trim(),
            Status = "active",
            // A ÚNICA porta que produz um workspace de time. Tudo o mais — inclusive cada linha que
            // já existia antes desta coluna — fica pessoal, e é isso que faz o convite e o PUT /key
            // serem recusados fora daqui.
            Kind = WorkspaceKinds.Team,
            CreatedAt = now,
        });
        db.Memberships.Add(new MembershipEntity
        {
            WorkspaceId = workspaceId,
            UserId = permCtx.UserId,
            // Quem cria o time é dono dele — é o único papel que consegue convidar E remover.
            Role = Roles.Owner,
            WrappedWk = wrapped,
            WkVersion = req.WkVersion,
        });
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: workspaceId,
            ActorUserId: permCtx.UserId,
            Action: "workspace.created",
            TargetType: "Workspace",
            TargetId: workspaceId,
            DeviceId: permCtx.DeviceId,
            // Só metadado: o embrulho NUNCA entra em log nem em auditoria (ADR-013).
            Metadata: new Dictionary<string, object?> { ["wkVersion"] = req.WkVersion }), ct);

        logger.LogInformation(
            "Team workspace {WorkspaceId} created by user {UserId} in tenant {TenantId} (wkVersion {Version})",
            workspaceId, permCtx.UserId, tenantId, req.WkVersion);

        return (
            CreateWorkspaceOutcome.Created,
            new CreateWorkspaceResponse(workspaceId.ToString(), req.Name.Trim(), Roles.Owner));
    }

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
    /// <para>⚠️ <c>null</c> (→ 404) diz UMA coisa só: <b>esta membership não tem WK guardada</b>.
    /// Não diz que o workspace é pessoal, e o doc que dizia isso alimentou um bloqueante no cliente
    /// — o mesmo 404 sai de um TIME cujo dono nunca publicou o embrulho dele. Quem afirma a natureza
    /// do workspace é <c>workspaces.kind</c>, que viaja na lista do login
    /// (<c>TokenService.LoadWorkspacesAsync</c>).</para>
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

    // ── PUT /workspaces/{id}/key ──────────────────────────────────────────────

    /// <summary>
    /// Publica o embrulho da chave do time <b>de quem chama</b> — e de mais ninguém: não existe
    /// usuário-alvo no pedido, o alvo é sempre <c>permCtx.UserId</c>. Exige
    /// <see cref="Permissions.SyncPull"/>, a mesma do <see cref="GetWorkspaceKeyAsync"/>: quem pode
    /// baixar o cofre precisa da chave que o abre, e é dele o embrulho que sobe.
    ///
    /// <para><b>O buraco que isto fecha:</b> até aqui, só o ACEITE do convite escrevia
    /// <c>WrappedWk</c>. Quem CRIA o time nunca subia o próprio embrulho, então o <c>GET</c> devolvia
    /// 404 para o dono — no segundo computador dele não havia o que restaurar, o chaveiro sorteava
    /// outra WK e o cofre do time bifurcava, com o indicador ainda dizendo "cofre pessoal".</para>
    ///
    /// <para><b>Primeira gravação vence; a segunda divergente é RECUSADA (não ignorada).</b> O
    /// servidor não tem AMK nenhuma, então ele não consegue — e não pode passar a conseguir, sob
    /// pena de matar o E2EE — distinguir "mesma chave, nonce novo" de "outra chave". O que ele
    /// consegue é comparar BYTES: iguais = republicação (no-op idempotente, o caso do reparo de
    /// boot); diferentes = ambíguo. Entre recusar e ignorar, recusar é o único desfecho honesto:
    /// ignorar responderia "tudo certo" para um device que talvez esteja com a chave errada, e essa
    /// é justamente a falha silenciosa que esta fatia inteira existe para matar. Com o 409, a
    /// ambiguidade volta para quem TEM como resolvê-la — o cliente, que baixa o embrulho guardado,
    /// abre com a própria AMK e compara as chaves de verdade. Trocar em silêncio nunca é opção: o
    /// blob antigo é o que os OUTROS devices daquele membro vão restaurar.</para>
    /// </summary>
    public async Task<PublishWorkspaceKeyOutcome> PublishWorkspaceKeyAsync(
        Guid workspaceId, PublishWorkspaceKeyRequest req, PermissionContext permCtx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        await RequireAsync(permCtx, Permissions.SyncPull, workspaceId, "workspace key publish", ct);

        // ⚠️ DEPOIS do RBAC, sempre (ver TeamWorkspaceGuard). Chave de time num cofre PESSOAL não é
        // só inútil: o blob guardado aqui é o que o aplicativo lê para decidir "este workspace é de
        // time", então plantá-lo faria o app passar a tratar o cofre pessoal do operador — com os
        // ~700 clientes dele — como cofre compartilhado. Nenhum fluxo legítimo chega aqui: quem só
        // tem cofre pessoal não tem chave de time em disco e o cliente sequer sai para a rede.
        await TeamWorkspaceGuard.RequireTeamAsync(
            db,
            workspaceId,
            "workspace.key-publish",
            "Guardar uma chave de time nele faria este computador passar a tratar o seu cofre "
            + "pessoal como cofre compartilhado.",
            logger,
            ct);

        var wrapped = WrappedKeyBlob.Decode(req.WrappedWk, nameof(req.WrappedWk));

        // Versão desde o dia 1 (mesma regra do convite): zero significa "membership sem chave", e
        // gravar um embrulho carimbado assim tornaria uma rotação futura indetectável.
        if (req.WkVersion < 1)
            throw new ArgumentException("wkVersion deve ser >= 1.");

        var membership = await db.Memberships
            .FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == permCtx.UserId, ct);

        // O RBAC já recusa quem não é membro (membership.missing). Se chegou aqui sem linha, o
        // modelo está inconsistente — melhor 403 do que criar membership por um PUT de chave.
        if (membership is null)
            throw new RbacDeniedException("membership.missing");

        if (membership.WrappedWk is { } existing)
        {
            // Comparação simples, e não em tempo constante de propósito: o blob não é segredo para
            // o servidor (ele o guarda e o devolve no GET) e quem chama acabou de mandá-lo. O que é
            // segredo — o código do convite — esse sim é comparado em tempo constante, no
            // InviteService.
            if (existing.AsSpan().SequenceEqual(wrapped) && membership.WkVersion == req.WkVersion)
            {
                return PublishWorkspaceKeyOutcome.AlreadyPublished;
            }

            logger.LogWarning(
                "Workspace key publish REFUSED for user {UserId} in workspace {WorkspaceId}: a "
                + "different wrap is already stored (stored v{StoredVersion}, offered v{OfferedVersion}). "
                + "Possible vault fork — the client must reconcile against the stored wrap.",
                permCtx.UserId, workspaceId, membership.WkVersion, req.WkVersion);
            return PublishWorkspaceKeyOutcome.Divergent;
        }

        membership.WrappedWk = wrapped;
        membership.WkVersion = req.WkVersion;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: workspaceId,
            ActorUserId: permCtx.UserId,
            Action: "workspace.key-published",
            TargetType: "Membership",
            TargetId: permCtx.UserId,
            DeviceId: permCtx.DeviceId,
            // Só metadado: nada aqui reconstrói o embrulho, e o embrulho não abre sem a AMK (ADR-013).
            Metadata: new Dictionary<string, object?> { ["wkVersion"] = req.WkVersion }), ct);

        logger.LogInformation(
            "Workspace key published by user {UserId} in workspace {WorkspaceId} (wkVersion {Version})",
            permCtx.UserId, workspaceId, req.WkVersion);

        return PublishWorkspaceKeyOutcome.Stored;
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
