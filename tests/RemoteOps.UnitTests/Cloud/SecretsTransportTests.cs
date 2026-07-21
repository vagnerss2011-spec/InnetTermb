using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Secrets;
using RemoteOps.Cloud.Sync;
using RemoteOps.Contracts.Sync;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Testes do transporte de SecretEnvelope (T5): upsert/pull de blob OPACO + RBAC + monotonicidade.
///
/// LIMITAÇÃO CONHECIDA: sem infra de Postgres no repo, roda contra AppDbContext
/// InMemory. O índice único (WorkspaceId, Cursor) — que é o que transforma uma
/// corrida de upsert concorrente em 409 em vez de perda silenciosa de dado — NÃO
/// é enforçado pelo provider InMemory, então esse caminho não é coberto aqui.
/// </summary>
public sealed class SecretsTransportTests
{
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static SecretEnvelopeDto NewEnvelope(Guid id, Guid workspaceId, int version = 1) =>
        new(
            Id: id.ToString(),
            WorkspaceId: workspaceId.ToString(),
            Ciphertext: Convert.ToBase64String(Rand(120)),
            Nonce: Convert.ToBase64String(Rand(12)),
            Tag: Convert.ToBase64String(Rand(16)),
            WrappedCek: Convert.ToBase64String(Rand(60)),
            CekNonce: Convert.ToBase64String(Rand(12)),
            CekTag: Convert.ToBase64String(Rand(16)),
            KeyVersion: "wdk-v1",
            Version: version);

    /// <summary>
    /// O que o cofre do cliente sobe ao revogar: material TODO vazio + a marca <c>revokedAt</c>.
    /// </summary>
    private static SecretEnvelopeDto Tombstone(Guid id, Guid workspaceId, int version, DateTimeOffset revokedAt) =>
        new(
            Id: id.ToString(),
            WorkspaceId: workspaceId.ToString(),
            Ciphertext: string.Empty,
            Nonce: string.Empty,
            Tag: string.Empty,
            WrappedCek: string.Empty,
            CekNonce: string.Empty,
            CekTag: string.Empty,
            KeyVersion: "wdk-v1",
            Version: version)
        {
            RevokedAt = revokedAt,
        };

    // ── Round-trip do blob opaco ──────────────────────────────────────────────

    [Fact]
    public async Task Upsert_Then_Pull_RoundTripsOpaqueBlob()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var dto = NewEnvelope(Guid.NewGuid(), ws.Id);
        var upserted = await ctx.Secrets.UpsertAsync(ws.Id, dto, permCtx, default);
        Assert.Equal("ok", upserted.Status);

        var pull = await ctx.Secrets.PullAsync(ws.Id, 0, 200, permCtx, default);

        var got = Assert.Single(pull.Envelopes);
        // O servidor devolve exatamente o que recebeu — byte a byte, sem interpretar.
        Assert.Equal(dto.Id, got.Id);
        Assert.Equal(dto.Ciphertext, got.Ciphertext);
        Assert.Equal(dto.Nonce, got.Nonce);
        Assert.Equal(dto.Tag, got.Tag);
        Assert.Equal(dto.WrappedCek, got.WrappedCek);
        Assert.Equal(dto.CekNonce, got.CekNonce);
        Assert.Equal(dto.CekTag, got.CekTag);
        Assert.Equal(dto.KeyVersion, got.KeyVersion);
        Assert.Equal(1, got.Version);
        Assert.True(pull.NextCursor > 0);
        Assert.False(pull.HasMore);
    }

    [Fact]
    public async Task Upsert_UpdatesExistingEnvelope_InPlace()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id), permCtx, default);
        var v2 = NewEnvelope(id, ws.Id, version: 2);
        await ctx.Secrets.UpsertAsync(ws.Id, v2, permCtx, default);

        var pull = await ctx.Secrets.PullAsync(ws.Id, 0, 200, permCtx, default);

        // Upsert: 1 linha só, com o conteúdo novo.
        var got = Assert.Single(pull.Envelopes);
        Assert.Equal(2, got.Version);
        Assert.Equal(v2.Ciphertext, got.Ciphertext);
    }

    // ── Monotonicidade do cursor ──────────────────────────────────────────────

    [Fact]
    public async Task Cursor_IsMonotonic_AndUpdateAdvancesIt()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var idA = Guid.NewGuid();
        var a1 = await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(idA, ws.Id), permCtx, default);
        var b1 = await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(Guid.NewGuid(), ws.Id), permCtx, default);

        Assert.True(b1.Cursor > a1.Cursor);

        // Device novo baixa tudo.
        var all = await ctx.Secrets.PullAsync(ws.Id, 0, 200, permCtx, default);
        Assert.Equal(2, all.Envelopes.Count);
        Assert.Equal(b1.Cursor, all.NextCursor);

        // Nada novo desde o cursor atual.
        var empty = await ctx.Secrets.PullAsync(ws.Id, all.NextCursor, 200, permCtx, default);
        Assert.Empty(empty.Envelopes);
        Assert.Equal(all.NextCursor, empty.NextCursor);

        // Atualizar um envelope antigo tem que EMPURRAR o cursor pra frente, senão
        // um device que já sincronizou nunca veria a rotação da senha.
        var a2 = await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(idA, ws.Id, version: 2), permCtx, default);
        Assert.True(a2.Cursor > b1.Cursor);

        var delta = await ctx.Secrets.PullAsync(ws.Id, all.NextCursor, 200, permCtx, default);
        var only = Assert.Single(delta.Envelopes);
        Assert.Equal(idA.ToString(), only.Id);
        Assert.Equal(2, only.Version);
    }

    [Fact]
    public async Task Pull_Paginates_AndReportsHasMore()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        for (var i = 0; i < 5; i++)
            await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(Guid.NewGuid(), ws.Id), permCtx, default);

        var page1 = await ctx.Secrets.PullAsync(ws.Id, 0, 2, permCtx, default);
        Assert.Equal(2, page1.Envelopes.Count);
        Assert.True(page1.HasMore);

        var page2 = await ctx.Secrets.PullAsync(ws.Id, page1.NextCursor, 2, permCtx, default);
        Assert.Equal(2, page2.Envelopes.Count);
        Assert.True(page2.HasMore);

        var page3 = await ctx.Secrets.PullAsync(ws.Id, page2.NextCursor, 2, permCtx, default);
        Assert.Single(page3.Envelopes);
        Assert.False(page3.HasMore);
    }

    // ── Versão monotônica por envelope ────────────────────────────────────────

    [Fact]
    public async Task Upsert_RejectsStaleVersion()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id, version: 5), permCtx, default);

        // Device atrasado tenta sobrescrever com uma versão velha.
        var stale = NewEnvelope(id, ws.Id, version: 4);
        var result = await ctx.Secrets.UpsertAsync(ws.Id, stale, permCtx, default);

        Assert.Equal("conflict", result.Status);
        Assert.Equal(5, result.CurrentVersion);

        // O conteúdo bom continua lá.
        var pull = await ctx.Secrets.PullAsync(ws.Id, 0, 200, permCtx, default);
        Assert.Equal(5, Assert.Single(pull.Envelopes).Version);
    }

    [Fact]
    public async Task Upsert_RejectsSameVersion()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id, version: 1), permCtx, default);
        var result = await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id, version: 1), permCtx, default);

        Assert.Equal("conflict", result.Status);
    }

    // ── Revogação (tombstone) ─────────────────────────────────────────────────

    /// <summary>
    /// O tombstone precisa ATRAVESSAR o servidor. Antes ele era recusado (material vazio), e a
    /// consequência real não era um erro de transporte: o envelope antigo ficava vivo e decifrável
    /// no disco do outro device PARA SEMPRE, a cada troca de senha.
    /// </summary>
    [Fact]
    public async Task Upsert_AceitaTombstone_ComMaterialVazio_EPropagaARevogacao()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id), permCtx, default);
        var revokedAt = DateTimeOffset.UtcNow;
        var result = await ctx.Secrets.UpsertAsync(ws.Id, Tombstone(id, ws.Id, 2, revokedAt), permCtx, default);

        Assert.Equal("ok", result.Status);

        var pull = await ctx.Secrets.PullAsync(ws.Id, 0, 200, permCtx, default);
        var got = Assert.Single(pull.Envelopes);
        Assert.NotNull(got.RevokedAt);
        // O material tem que descer VAZIO: é ele que apaga o segredo no device que recebe.
        Assert.Equal(string.Empty, got.Ciphertext);
        Assert.Equal(string.Empty, got.WrappedCek);
    }

    /// <summary>
    /// A validação GERAL não afrouxa: envelope sem marca de revogação continua tendo que trazer
    /// material. Sem esta guarda, a brecha do tombstone viraria "qualquer corpo vazio serve".
    /// </summary>
    [Fact]
    public async Task Upsert_ContinuaRecusandoMaterialVazio_DeEnvelopeNaoRevogado()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);

        var vazio = NewEnvelope(Guid.NewGuid(), ws.Id) with { Ciphertext = string.Empty };

        await Assert.ThrowsAsync<ArgumentException>(
            () => ctx.Secrets.UpsertAsync(ws.Id, vazio, permCtx, default));
        Assert.Empty(ctx.Db.SecretEnvelopes);
    }

    /// <summary>
    /// Tombstone tem que ser VAZIO, não apenas PODER ser vazio (achado da revisão de segurança).
    ///
    /// <para>A liberação de material vazio existe só para a lápide; aceitar uma lápide COM material
    /// deixaria ciphertext circulando por baixo da marca de revogação — e um device que não entende o
    /// campo (versão anterior) o gravaria como envelope vivo. Hoje esse material não abriria (a versão
    /// entra no AAD), mas com a chave de workspace COMPARTILHADA da Fatia 1 isso vira ressurreição de
    /// senha revogada. Exigir vazio agora custa uma linha; depois custa um incidente.</para>
    /// </summary>
    [Fact]
    public async Task Upsert_RecusaTombstone_ComMaterialNaoVazio()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id), permCtx, default);

        // Lápide "suja": traz a marca de revogação E material — a combinação que não pode existir.
        var vivo = NewEnvelope(id, ws.Id) with { Version = 2 };
        var lapideSuja = vivo with { RevokedAt = DateTimeOffset.UtcNow };

        await Assert.ThrowsAsync<ArgumentException>(
            () => ctx.Secrets.UpsertAsync(ws.Id, lapideSuja, permCtx, default));

        // E o envelope vivo original segue intacto: a recusa não pode ter efeito colateral.
        var pull = await ctx.Secrets.PullAsync(ws.Id, 0, 200, permCtx, default);
        var got = Assert.Single(pull.Envelopes);
        Assert.Null(got.RevokedAt);
    }

    /// <summary>
    /// Revogação é caminho SÓ DE IDA. Aceitar uma cópia viva por cima de um tombstone republicaria
    /// a senha velha em todos os devices do workspace — o oposto do que a revogação existe para
    /// fazer.
    /// </summary>
    [Fact]
    public async Task Upsert_NuncaRessuscita_EnvelopeJaRevogado()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id), permCtx, default);
        await ctx.Secrets.UpsertAsync(ws.Id, Tombstone(id, ws.Id, 2, DateTimeOffset.UtcNow), permCtx, default);

        var result = await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id, version: 9), permCtx, default);

        Assert.Equal("conflict", result.Status);
        Assert.Equal("envelope.revoked", result.Reason);

        var stored = ctx.Db.SecretEnvelopes.Single(e => e.Id == id);
        Assert.NotNull(stored.RevokedAt);
        Assert.Empty(stored.Ciphertext);
    }

    /// <summary>
    /// A corrida que ressuscita senha revogada (pré-requisito de segurança da Fatia 1).
    ///
    /// <para>O upsert lê <c>existing</c> e só depois grava. Entre a leitura e a gravação, OUTRA
    /// requisição pode commitar a lápide. Sem token de concorrência, quem chegou primeiro grava por
    /// cima com as guardas decididas em cima de um estado que já não existe.</para>
    ///
    /// <para><b>O estrago medido é este</b> (e é mais sutil do que "o RevokedAt é zerado"): o EF só
    /// escreve as colunas MODIFICADAS, e para o upsert vivo o <c>RevokedAt</c> vai de nulo a nulo,
    /// então a marca em si sobrevive — mas <b>o material volta</b>. O envelope fica "revogado E com
    /// ciphertext", exatamente o estado que a v1.4.7 proibiu no servidor, agora com
    /// <c>Version</c>/<c>Cursor</c> novos, o que faz o servidor PROPAGAR isso para todos os devices.
    /// Um device de versão anterior, que não entende <c>RevokedAt</c>, grava o envelope como VIVO.
    /// Enquanto cada conta deriva a própria WDK esse material não abre no PC da vítima; com a chave
    /// de workspace COMPARTILHADA do time ele abre — por isso isto é bloqueante da Fatia 1.</para>
    ///
    /// <para>A encenação é fiel ao que acontece em produção: o contexto "A" materializa o envelope
    /// enquanto ele ainda está VIVO (é a leitura que o upsert faria), o contexto "B" commita a
    /// lápide, e só então "A" grava. Como o EF não sobrescreve valores de entidade já rastreada, o
    /// snapshot de "A" continua velho — exatamente o estado da corrida real.</para>
    /// </summary>
    [Fact]
    public async Task Upsert_Concorrente_NaoApagaALapide_QueChegouNoMeioDaCorrida()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id), permCtx, default);

        // Requisição A: lê o envelope AINDA VIVO (o `existing` do upsert dela).
        using var dbA = ctx.NewDbContext();
        var secretsA = CloudTestContext.SecretsOn(dbA);
        var vistoVivo = await dbA.SecretEnvelopes.FirstAsync(e => e.Id == id);
        Assert.Null(vistoVivo.RevokedAt);

        // Requisição B: a revogação chega e é commitada no meio da corrida.
        await ctx.Secrets.UpsertAsync(
            ws.Id, Tombstone(id, ws.Id, 2, DateTimeOffset.UtcNow), permCtx, default);

        // Requisição A termina e tenta gravar o envelope VIVO por cima da lápide.
        var resultado = await secretsA.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id, version: 3), permCtx, default);

        // O que importa de verdade, afirmado PRIMEIRO: a lápide SOBREVIVE, com o material zerado.
        // (Sem o token de concorrência é aqui que o teste acusa a ressurreição.)
        using var check = ctx.NewDbContext();
        var stored = await check.SecretEnvelopes.AsNoTracking().FirstAsync(e => e.Id == id);
        Assert.NotNull(stored.RevokedAt);
        Assert.Empty(stored.Ciphertext);
        Assert.Empty(stored.WrappedCek!);

        // E o upsert perdedor tem que FALHAR com conflito (409 → o outbox do cliente re-tenta e aí
        // bate na guarda `envelope.revoked`), nunca "ok".
        Assert.Equal("conflict", resultado.Status);
    }

    /// <summary>
    /// O token de concorrência não pode custar o caminho normal: dois upserts SEQUENCIAIS em
    /// contextos diferentes (o caso do dia a dia, dois devices em momentos distintos) seguem
    /// passando. Sem esta guarda, "proteger a lápide" viraria "nada sobe mais".
    /// </summary>
    [Fact]
    public async Task Upsert_EmContextosDiferentes_SemCorrida_ContinuaPassando()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var permCtx = new PermissionContext(user.Id, ws.Id);
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(ws.Id, NewEnvelope(id, ws.Id), permCtx, default);

        using var dbB = ctx.NewDbContext();
        var secretsB = CloudTestContext.SecretsOn(dbB);
        var v2 = NewEnvelope(id, ws.Id, version: 2);
        var resultado = await secretsB.UpsertAsync(ws.Id, v2, permCtx, default);

        Assert.Equal("ok", resultado.Status);

        using var check = ctx.NewDbContext();
        var stored = await check.SecretEnvelopes.AsNoTracking().FirstAsync(e => e.Id == id);
        Assert.Equal(2, stored.Version);
        Assert.Equal(v2.Ciphertext, Convert.ToBase64String(stored.Ciphertext));
    }

    // ── RBAC ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_Denied_ForUserWithoutWorkspaceMembership()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, owner, _) = await ctx.SeedActiveUserAsync("Owner");
        await ctx.Secrets.UpsertAsync(
            ws.Id, NewEnvelope(Guid.NewGuid(), ws.Id), new PermissionContext(owner.Id, ws.Id), default);

        // Usuário de OUTRO workspace tenta ler o cofre deste.
        var (_, _, stranger, _) = await ctx.SeedActiveUserAsync("Owner");
        var strangerCtx = new PermissionContext(stranger.Id, ws.Id);

        var ex = await Assert.ThrowsAsync<RbacDeniedException>(
            () => ctx.Secrets.PullAsync(ws.Id, 0, 200, strangerCtx, default));
        Assert.Equal("membership.missing", ex.Message);
    }

    [Fact]
    public async Task Upsert_Denied_ForUserWithoutWorkspaceMembership()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, _, _) = await ctx.SeedActiveUserAsync("Owner");
        var (_, _, stranger, _) = await ctx.SeedActiveUserAsync("Owner");

        await Assert.ThrowsAsync<RbacDeniedException>(
            () => ctx.Secrets.UpsertAsync(
                ws.Id, NewEnvelope(Guid.NewGuid(), ws.Id),
                new PermissionContext(stranger.Id, ws.Id), default));

        Assert.Empty(ctx.Db.SecretEnvelopes);
    }

    [Fact]
    public async Task Upsert_Denied_ForReadOnlyRole()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("ReadOnly");

        // ReadOnly tem sync.pull mas não sync.push.
        await Assert.ThrowsAsync<RbacDeniedException>(
            () => ctx.Secrets.UpsertAsync(
                ws.Id, NewEnvelope(Guid.NewGuid(), ws.Id),
                new PermissionContext(user.Id, ws.Id), default));
    }

    [Fact]
    public async Task Pull_Allowed_ForOperatorRole()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Operator");

        // Operator tem credential.use — precisa baixar o cofre pra abrir sessão.
        var pull = await ctx.Secrets.PullAsync(ws.Id, 0, 200, new PermissionContext(user.Id, ws.Id), default);
        Assert.Empty(pull.Envelopes);
    }

    [Fact]
    public async Task Pull_Denied_ForRevokedDevice()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");

        var device = new RemoteOps.Cloud.Data.Entities.DeviceEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "Laptop Roubado",
            Status = "revoked",
            RegisteredAt = DateTimeOffset.UtcNow,
            RevokedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Devices.Add(device);
        await ctx.Db.SaveChangesAsync();

        // Device revogado não pode baixar o cofre nem com token válido.
        var ex = await Assert.ThrowsAsync<RbacDeniedException>(
            () => ctx.Secrets.PullAsync(ws.Id, 0, 200,
                new PermissionContext(user.Id, ws.Id, device.Id), default));
        Assert.Equal("device.revoked", ex.Message);
    }

    // ── Isolamento entre workspaces ───────────────────────────────────────────

    [Fact]
    public async Task Upsert_Rejects_WhenEnvelopeBelongsToAnotherWorkspace()
    {
        using var ctx = new CloudTestContext();
        var (_, wsA, user, _) = await ctx.SeedActiveUserAsync("Owner");
        var (_, wsB, _, _) = await ctx.SeedActiveUserAsync("Owner");
        var id = Guid.NewGuid();

        await ctx.Secrets.UpsertAsync(
            wsA.Id, NewEnvelope(id, wsA.Id), new PermissionContext(user.Id, wsA.Id), default);

        // Owner do wsB dá membership pro user e tenta sequestrar o id do envelope do wsA.
        ctx.Db.Memberships.Add(new RemoteOps.Cloud.Data.Entities.MembershipEntity
        {
            WorkspaceId = wsB.Id,
            UserId = user.Id,
            Role = "Owner",
        });
        await ctx.Db.SaveChangesAsync();

        var hijack = NewEnvelope(id, wsB.Id, version: 99);
        var result = await ctx.Secrets.UpsertAsync(
            wsB.Id, hijack, new PermissionContext(user.Id, wsB.Id), default);

        Assert.Equal("conflict", result.Status);

        // O envelope do wsA continua intacto.
        var stillA = ctx.Db.SecretEnvelopes.Single(e => e.Id == id);
        Assert.Equal(wsA.Id, stillA.WorkspaceId);
        Assert.Equal(1, stillA.Version);
    }

    [Fact]
    public async Task Pull_OnlyReturnsEnvelopesOfRequestedWorkspace()
    {
        using var ctx = new CloudTestContext();
        var (_, wsA, userA, _) = await ctx.SeedActiveUserAsync("Owner");
        var (_, wsB, userB, _) = await ctx.SeedActiveUserAsync("Owner");

        await ctx.Secrets.UpsertAsync(
            wsA.Id, NewEnvelope(Guid.NewGuid(), wsA.Id), new PermissionContext(userA.Id, wsA.Id), default);
        await ctx.Secrets.UpsertAsync(
            wsB.Id, NewEnvelope(Guid.NewGuid(), wsB.Id), new PermissionContext(userB.Id, wsB.Id), default);

        var pullA = await ctx.Secrets.PullAsync(wsA.Id, 0, 200, new PermissionContext(userA.Id, wsA.Id), default);
        var onlyA = Assert.Single(pullA.Envelopes);
        Assert.Equal(wsA.Id.ToString(), onlyA.WorkspaceId);
    }

    // ── O changelog continua sem segredo ──────────────────────────────────────

    [Fact]
    public async Task SyncPush_StillRejects_SecretEnvelopeInChangelog()
    {
        using var ctx = new CloudTestContext();
        var (_, ws, user, _) = await ctx.SeedActiveUserAsync("Owner");

        // O blob viaja por /secrets; o changelog de metadados segue recusando.
        var result = await ctx.Sync.PushAsync(ws.Id, [new SyncChange
        {
            EntityType = "SecretEnvelope",
            EntityId = Guid.NewGuid().ToString(),
            Operation = "created",
            BaseVersion = 0,
            Patch = [],
        }], new PermissionContext(user.Id, ws.Id), default);

        Assert.Equal("conflict", result.Status);
        Assert.Equal("secret-envelope.no-auto-merge", Assert.Single(result.Conflicts!).Reason);
    }
}
