using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Secrets;
using RemoteOps.Cloud.Sync;
using RemoteOps.Cloud.Teams;
using RemoteOps.Security.Account;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Convite de time (Fatia 1, estágio 1c). O que está sendo protegido aqui NÃO é o caminho feliz — é
/// a promessa de E2EE: o servidor guarda um HASH e um BLOB, e nenhuma resposta dele pode servir de
/// oráculo (existe convite para esse e-mail? o código está quase certo?).
///
/// <para>InMemory + serviços reais, no padrão do <see cref="CloudTestContext"/>. O provider InMemory
/// enforça token de concorrência, que é o que torna o teste de aceite concorrente honesto.</para>
/// </summary>
public sealed class TeamInviteTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>Embrulho da WK como o cliente manda: nonce(12)+tag(16)+ciphertext(32) = 60B opacos.</summary>
    private static string WrappedBlob() => Convert.ToBase64String(Rand(60));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private const string InviteeEmail = "colega@isp.local";

    /// <summary>
    /// Time montado: dono (Owner, com membership e WK) + a conta do convidado, ainda de fora.
    /// Devolve também o contexto de permissão do dono, que é o que os endpoints passam ao serviço.
    /// </summary>
    private static async Task<(WorkspaceEntity Ws, UserEntity Owner, UserEntity Invitee, PermissionContext OwnerCtx)>
        SeedTeamAsync(CloudTestContext ctx, string ownerRole = Roles.Owner)
    {
        var (_, ws, owner, membership) = await ctx.SeedActiveUserAsync(ownerRole);
        membership.WrappedWk = Rand(60);
        membership.WkVersion = 1;
        await ctx.Db.SaveChangesAsync();

        var invitee = await ctx.SeedAccountAsync(InviteeEmail);
        return (ws, owner, invitee, new PermissionContext(owner.Id, ws.Id));
    }

    private static CreateInviteRequest NewInvite(string code, string role = Roles.Manager) =>
        new(InviteeEmail, role, Sha256Hex(code), WrappedBlob(), WkVersion: 1);

    // ── Caminho feliz ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Aceite_CriaMembership_ComAChaveEmbrulhadaPeloConvidado()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();

        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);

        // 1) O convidado troca o código pelo blob (o servidor não abre nada — só devolve).
        var invite = await ctx.Invites.GetContextAsync(
            Guid.Parse(created.InviteId), Sha256Hex(code), invitee.Id, default);
        Assert.NotNull(invite);
        Assert.Equal(ws.Id.ToString(), invite.WorkspaceId);
        Assert.Equal(Roles.Manager, invite.Role);

        // 2) Desembrulhou com K_invite e re-embrulhou sob a PRÓPRIA AMK — sobe só o blob novo.
        var rewrapped = WrappedBlob();
        var accepted = await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), rewrapped), invitee.Id, default);

        Assert.NotNull(accepted);
        Assert.Equal(ws.Id.ToString(), accepted.WorkspaceId);

        var membership = await ctx.Db.Memberships
            .SingleAsync(m => m.WorkspaceId == ws.Id && m.UserId == invitee.Id);
        Assert.Equal(Roles.Manager, membership.Role);
        Assert.Equal(Convert.FromBase64String(rewrapped), membership.WrappedWk);
        // A versão vem do CONVITE, não do corpo: o cliente não pode declarar a versão que quiser.
        Assert.Equal(1, membership.WkVersion);
    }

    [Fact]
    public async Task Convite_MarcaQuemAceitou_EQuandoQuem()
    {
        using var ctx = new CloudTestContext();
        var (ws, owner, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();

        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()), invitee.Id, default);

        var invite = await ctx.Db.Invites.SingleAsync();
        Assert.NotNull(invite.AcceptedAt);
        Assert.Equal(invitee.Id, invite.AcceptedByUserId);
        Assert.Equal(owner.Id, invite.InvitedByUserId);
    }

    // ── Expiração ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convite_Expira_EOContextoEOAceiteRecusam()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();

        var now = CloudTestContext.FixedNow;
        var svc = ctx.InvitesAt(() => now);
        var created = await svc.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        var inviteId = Guid.Parse(created.InviteId);

        // Ainda dentro do prazo.
        Assert.NotNull(await svc.GetContextAsync(inviteId, Sha256Hex(code), invitee.Id, default));

        now = created.ExpiresAt.AddSeconds(1);

        Assert.Null(await svc.GetContextAsync(inviteId, Sha256Hex(code), invitee.Id, default));
        Assert.Null(await svc.AcceptAsync(
            inviteId, new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()), invitee.Id, default));
        Assert.Empty(ctx.Db.Memberships.Where(m => m.UserId == invitee.Id));
    }

    // ── Uso único ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Convite_EhDeUsoUnico_SegundoAceiteFalha()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        var inviteId = Guid.Parse(created.InviteId);

        Assert.NotNull(await ctx.Invites.AcceptAsync(
            inviteId, new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()), invitee.Id, default));

        // Convite queimado: nem aceita de novo, nem devolve o blob para quem tiver o código depois.
        Assert.Null(await ctx.Invites.AcceptAsync(
            inviteId, new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()), invitee.Id, default));
        Assert.Null(await ctx.Invites.GetContextAsync(inviteId, Sha256Hex(code), invitee.Id, default));
    }

    // ── Anti-enumeração ───────────────────────────────────────────────────────

    [Fact]
    public async Task CodigoErrado_NaoRevelaNada_MesmaRespostaDoInexistente()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        var inviteId = Guid.Parse(created.InviteId);

        // Convite REAL com código errado, convite inexistente, convite expirado e convite já usado
        // têm que ser indistinguíveis: qualquer diferença vira oráculo de "esse e-mail tem convite".
        var codigoErrado = await ctx.Invites.GetContextAsync(
            inviteId, Sha256Hex(RecoveryKeyCodec.Generate()), invitee.Id, default);
        var inexistente = await ctx.Invites.GetContextAsync(
            Guid.NewGuid(), Sha256Hex(code), invitee.Id, default);

        Assert.Null(codigoErrado);
        Assert.Null(inexistente);

        // E o aceite com código errado não pode criar nada nem queimar o convite bom.
        Assert.Null(await ctx.Invites.AcceptAsync(
            inviteId, new AcceptInviteRequest(Sha256Hex(RecoveryKeyCodec.Generate()), WrappedBlob()),
            invitee.Id, default));
        Assert.Empty(ctx.Db.Memberships.Where(m => m.UserId == invitee.Id));
        Assert.Null(ctx.Db.Invites.Single().AcceptedAt);
    }

    [Fact]
    public async Task Convite_SoServeParaOEmailConvidado()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, _, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);

        // Outra conta que por acaso soube o código não entra no cofre do time.
        var estranho = await ctx.SeedAccountAsync("estranho@isp.local");

        Assert.Null(await ctx.Invites.GetContextAsync(
            Guid.Parse(created.InviteId), Sha256Hex(code), estranho.Id, default));
        Assert.Null(await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()),
            estranho.Id, default));
        Assert.Empty(ctx.Db.Memberships.Where(m => m.UserId == estranho.Id));
    }

    // ── RBAC ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NaoMembro_NaoConvida()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, _, _) = await SeedTeamAsync(ctx);
        var forasteiro = await ctx.SeedAccountAsync("forasteiro@isp.local");
        var code = RecoveryKeyCodec.Generate();

        await Assert.ThrowsAsync<RbacDeniedException>(() => ctx.Invites.CreateAsync(
            ws.Id, NewInvite(code), new PermissionContext(forasteiro.Id, ws.Id), default));

        Assert.Empty(ctx.Db.Invites);
    }

    [Fact]
    public async Task MembroSemPermissaoDeConvite_NaoConvida()
    {
        using var ctx = new CloudTestContext();
        // Operator não tem user.invite (ver Roles.BuildOperator).
        var (ws, _, _, ownerCtx) = await SeedTeamAsync(ctx, Roles.Operator);
        var code = RecoveryKeyCodec.Generate();

        await Assert.ThrowsAsync<RbacDeniedException>(() =>
            ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default));

        Assert.Empty(ctx.Db.Invites);
    }

    [Fact]
    public async Task NaoConvidaComPapelMaisPoderosoQueODeQuemConvida()
    {
        using var ctx = new CloudTestContext();
        // Manager tem user.invite, mas não pode fabricar um Owner e ser removido por ele em seguida.
        var (ws, _, _, ownerCtx) = await SeedTeamAsync(ctx, Roles.Manager);
        var code = RecoveryKeyCodec.Generate();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ctx.Invites.CreateAsync(ws.Id, NewInvite(code, Roles.Owner), ownerCtx, default));

        Assert.Empty(ctx.Db.Invites);
    }

    // ── Formato do que sobe (a fronteira E2EE) ────────────────────────────────

    [Fact]
    public async Task RecusaCodigoCru_NoLugarDoHash()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, _, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();

        // Um cliente que mandasse o CÓDIGO onde vai o hash entregaria o cofre do time ao servidor.
        // Isso tem que quebrar ALTO — não ser gravado como se fosse hash.
        var comCodigoCru = new CreateInviteRequest(InviteeEmail, Roles.Manager, code, WrappedBlob(), 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ctx.Invites.CreateAsync(ws.Id, comCodigoCru, ownerCtx, default));

        Assert.Empty(ctx.Db.Invites);
    }

    [Fact]
    public async Task NaoConvidaQuemJaEMembro()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()),
            invitee.Id, default);

        // Aqui a verdade AJUDA: quem pergunta é o dono, sobre o próprio time, e o convite que ele
        // mandaria nunca funcionaria (a membership já existe). Não é enumeração — ele lista membros.
        await Assert.ThrowsAsync<InviteConflictException>(() =>
            ctx.Invites.CreateAsync(ws.Id, NewInvite(RecoveryKeyCodec.Generate()), ownerCtx, default));
    }

    [Fact]
    public async Task ConviteNovo_SupersedeOPendenteDoMesmoEmail()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var codigoAntigo = RecoveryKeyCodec.Generate();
        var antigo = await ctx.Invites.CreateAsync(ws.Id, NewInvite(codigoAntigo), ownerCtx, default);

        var codigoNovo = RecoveryKeyCodec.Generate();
        var novo = await ctx.Invites.CreateAsync(ws.Id, NewInvite(codigoNovo), ownerCtx, default);

        // Um código repassado por WhatsApp e depois substituído tem que ENVELHECER: só o convite
        // mais novo vale (mesma disciplina do reset de senha).
        Assert.Null(await ctx.Invites.GetContextAsync(
            Guid.Parse(antigo.InviteId), Sha256Hex(codigoAntigo), invitee.Id, default));
        Assert.NotNull(await ctx.Invites.GetContextAsync(
            Guid.Parse(novo.InviteId), Sha256Hex(codigoNovo), invitee.Id, default));
        Assert.Single(ctx.Db.Invites);
    }

    [Fact]
    public async Task WorkspaceDesativado_NaoRecebeGenteNova()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);

        ws.Status = "suspended";
        await ctx.Db.SaveChangesAsync();

        // Todo o resto do sistema recusa workspace inativo; o convite não pode ser a porta que
        // continua aberta.
        Assert.Null(await ctx.Invites.GetContextAsync(
            Guid.Parse(created.InviteId), Sha256Hex(code), invitee.Id, default));
        Assert.Null(await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()),
            invitee.Id, default));
    }

    [Fact]
    public async Task Codigo_NuncaAparece_EmLogNemNaRespostaDaCriacao()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var req = NewInvite(code);

        var created = await ctx.Invites.CreateAsync(ws.Id, req, ownerCtx, default);
        await ctx.Invites.GetContextAsync(Guid.Parse(created.InviteId), Sha256Hex(code), invitee.Id, default);
        await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()),
            invitee.Id, default);

        // A resposta de criação: nada de código, nada de hash, nada do blob.
        var json = JsonSerializer.Serialize(created);
        Assert.DoesNotContain(code, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(req.CodeHash, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(req.WrappedWkByInvite, json, StringComparison.Ordinal);

        // E o log (ADR-013): só GUID/id. O hash é credencial de portador do blob — também não vai.
        var logs = ctx.InviteLogger.AllText;
        Assert.DoesNotContain(code, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(req.CodeHash, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(req.WrappedWkByInvite, logs, StringComparison.Ordinal);
        // Nem o e-mail do convidado em claro — é dado pessoal, e o padrão da base é hash curto.
        Assert.DoesNotContain(InviteeEmail, logs, StringComparison.OrdinalIgnoreCase);
    }

    // ── E-mail ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Email_LevaOLink_NuncaOCodigo_EDizQueOCodigoVemPorOutroCanal()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, _, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();

        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);

        var msg = Assert.Single(ctx.Email.Sent);
        Assert.Equal(InviteeEmail, msg.ToEmail);
        Assert.Contains(created.InviteId, msg.TextBody, StringComparison.Ordinal);
        // Se o código viajasse junto do link, quem lê o e-mail entra no cofre do time — o E2EE
        // inteiro viraria teatro. O enviador default (LoggingEmailSender) ainda joga o corpo no log.
        Assert.DoesNotContain(code, msg.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sha256Hex(code), msg.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outro canal", msg.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(created.EmailDelivered);
    }

    [Fact]
    public async Task FalhaDeEmail_NaoDerrubaOConvite_EAvisaQuemConvidou()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        ctx.Email.FailNext = true;

        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);

        // O convite VALE: o dono repassa link e código na mão. Mas a tela precisa saber que o
        // e-mail não saiu — silenciar isso deixaria o dono esperando para sempre.
        Assert.False(created.EmailDelivered);
        Assert.NotNull(await ctx.Invites.GetContextAsync(
            Guid.Parse(created.InviteId), Sha256Hex(code), invitee.Id, default));
        Assert.Contains(ctx.InviteLogger.Messages, m => m.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    // ── Corrida ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AceiteConcorrente_NaoCriaMembershipDupla()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        var inviteId = Guid.Parse(created.InviteId);

        // Dois contextos = duas requisições HTTP. As duas leem o convite PENDENTE ANTES de qualquer
        // gravação — é isso que faz disto uma corrida de verdade: quando a segunda for gravar, a
        // guarda em memória dela ainda vê AcceptedAt nulo, e só o banco pode barrar. Quem barra
        // AQUI é a chave primária da membership; a trava da linha do convite tem teste próprio
        // (Convite_TemTokenDeConcorrencia_NoAcceptedAt).
        using var dbA = ctx.NewDbContext();
        using var dbB = ctx.NewDbContext();
        await dbA.Invites.FirstAsync(i => i.Id == inviteId);
        await dbB.Invites.FirstAsync(i => i.Id == inviteId);
        var a = ctx.InvitesOn(dbA);
        var b = ctx.InvitesOn(dbB);

        var reqA = new AcceptInviteRequest(Sha256Hex(code), WrappedBlob());
        var reqB = new AcceptInviteRequest(Sha256Hex(code), WrappedBlob());

        var okA = await a.AcceptAsync(inviteId, reqA, invitee.Id, default);
        var okB = await b.AcceptAsync(inviteId, reqB, invitee.Id, default);

        // Exatamente um aceite vale, e existe EXATAMENTE uma membership.
        Assert.True(okA is null ^ okB is null);
        Assert.Single(ctx.Db.Memberships.Where(m => m.WorkspaceId == ws.Id && m.UserId == invitee.Id));
    }

    [Fact]
    public async Task Convite_TemTokenDeConcorrencia_NoAcceptedAt()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, _, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        var inviteId = Guid.Parse(created.InviteId);

        // Prova DIRETA do que o teste acima NÃO prova: quem barra a segunda membership é a chave
        // primária (o convite é de UM e-mail, logo de UMA conta). A linha do CONVITE tem a própria
        // trava — sem ela, quem perdeu a corrida remarcaria AcceptedAt/AcceptedByUserId por cima do
        // aceite que valeu, e a auditoria passaria a apontar para a pessoa errada.
        using var dbA = ctx.NewDbContext();
        using var dbB = ctx.NewDbContext();
        var inviteA = await dbA.Invites.FirstAsync(i => i.Id == inviteId);
        var inviteB = await dbB.Invites.FirstAsync(i => i.Id == inviteId);

        inviteA.AcceptedAt = CloudTestContext.FixedNow;
        await dbA.SaveChangesAsync();

        inviteB.AcceptedAt = CloudTestContext.FixedNow.AddMinutes(1);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    // ── Membros ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListaMembros_MostraQuemJaTemAChave()
    {
        using var ctx = new CloudTestContext();
        var (ws, owner, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()),
            invitee.Id, default);

        var members = await ctx.Team.ListMembersAsync(ws.Id, ownerCtx, default);

        Assert.Equal(2, members.Members.Count);
        Assert.Contains(members.Members, m => m.UserId == owner.Id.ToString() && m.Role == Roles.Owner);
        var novo = Assert.Single(members.Members, m => m.UserId == invitee.Id.ToString());
        Assert.Equal(InviteeEmail, novo.Email);
        Assert.True(novo.HasWk);
        Assert.Equal(1, novo.WkVersion);
    }

    [Fact]
    public async Task RemoverMembro_CortaOAcesso_PullEPushPassamANegar()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), WrappedBlob()),
            invitee.Id, default);

        var inviteeCtx = new PermissionContext(invitee.Id, ws.Id);

        // Antes: o membro baixa o cofre.
        var antes = await ctx.Secrets.PullAsync(ws.Id, 0, 50, inviteeCtx, default);
        Assert.NotNull(antes);

        Assert.Equal(RemoveMemberOutcome.Removed,
            await ctx.Team.RemoveMemberAsync(ws.Id, invitee.Id, ownerCtx, default));

        // Depois: pull E push negam — o corte vale do próximo request em diante.
        await Assert.ThrowsAsync<RbacDeniedException>(() =>
            ctx.Secrets.PullAsync(ws.Id, 0, 50, inviteeCtx, default));
        await Assert.ThrowsAsync<RbacDeniedException>(() =>
            ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(ws.Id), inviteeCtx, default));

        Assert.Empty(ctx.Db.Memberships.Where(m => m.WorkspaceId == ws.Id && m.UserId == invitee.Id));
    }

    [Fact]
    public async Task NaoRemoveOUltimoDono()
    {
        using var ctx = new CloudTestContext();
        var (ws, owner, _, ownerCtx) = await SeedTeamAsync(ctx);

        // Sem dono, ninguém mais administra o workspace — e não há tela que conserte isso.
        Assert.Equal(RemoveMemberOutcome.LastOwner,
            await ctx.Team.RemoveMemberAsync(ws.Id, owner.Id, ownerCtx, default));
        Assert.Single(ctx.Db.Memberships.Where(m => m.WorkspaceId == ws.Id));
    }

    [Fact]
    public async Task RemoverQuemNaoEMembro_NaoDizQueRemoveu()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);

        Assert.Equal(RemoveMemberOutcome.NotAMember,
            await ctx.Team.RemoveMemberAsync(ws.Id, invitee.Id, ownerCtx, default));
    }

    [Fact]
    public async Task ChaveDoWorkspace_SoDevolveOEmbrulhoDeQuemPergunta()
    {
        using var ctx = new CloudTestContext();
        var (ws, _, invitee, ownerCtx) = await SeedTeamAsync(ctx);
        var code = RecoveryKeyCodec.Generate();
        var created = await ctx.Invites.CreateAsync(ws.Id, NewInvite(code), ownerCtx, default);
        var rewrapped = WrappedBlob();
        await ctx.Invites.AcceptAsync(
            Guid.Parse(created.InviteId), new AcceptInviteRequest(Sha256Hex(code), rewrapped),
            invitee.Id, default);

        // É isto que faz o SEGUNDO device do convidado abrir o cofre: a AMK é portável, o blob
        // guardado em disco não é.
        var key = await ctx.Team.GetWorkspaceKeyAsync(ws.Id, new PermissionContext(invitee.Id, ws.Id), default);

        Assert.NotNull(key);
        Assert.Equal(rewrapped, key.WrappedWk);
        Assert.Equal(1, key.WkVersion);
    }

    private static SecretEnvelopeDto NewEnvelope(Guid workspaceId) => new(
        Id: Guid.NewGuid().ToString(),
        WorkspaceId: workspaceId.ToString(),
        Ciphertext: Convert.ToBase64String(Rand(48)),
        Nonce: Convert.ToBase64String(Rand(12)),
        Tag: Convert.ToBase64String(Rand(16)),
        WrappedCek: Convert.ToBase64String(Rand(60)),
        CekNonce: Convert.ToBase64String(Rand(12)),
        CekTag: Convert.ToBase64String(Rand(16)),
        KeyVersion: "wk-v1",
        Version: 1);
}
