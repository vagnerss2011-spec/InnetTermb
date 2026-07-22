using Microsoft.AspNetCore.Mvc;
using RemoteOps.Cloud.Errors;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Teams;

/// <summary>
/// Endpoints do time. Dois grupos, e a divisão NÃO é estética:
///
/// <list type="bullet">
/// <item><c>/workspaces/{id}/...</c> — quem já é do time (RBAC pelo PermissionEvaluator, igual ao
/// /sync e ao /secrets).</item>
/// <item><c>/invites/{id}/...</c> — o convidado, que por definição AINDA NÃO é membro. Se o aceite
/// morasse debaixo de <c>/workspaces/{id}</c>, qualquer guarda de "tem que ser membro" aplicada ao
/// grupo (hoje ou num refactor futuro) trancaria justamente quem o convite existe para deixar
/// entrar. Aqui a autorização é o próprio convite: id + prova do código + e-mail batendo.</item>
/// </list>
/// </summary>
public static class TeamEndpoints
{
    /// <summary>Recusa ÚNICA do fluxo de convite — a mesma para inexistente, expirado, usado, de outro e-mail ou código errado.</summary>
    private const string InviteRefusal =
        "Convite inválido, expirado ou já utilizado. Confira o link e o código com quem convidou.";

    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        MapWorkspaceScoped(app);
        MapInviteeScoped(app);
        return app;
    }

    private static void MapWorkspaceScoped(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}").WithTags("Teams").RequireAuthorization();

        // ── POST /workspaces/{id}/invites ─────────────────────────────────────
        group.MapPost("/invites", async (
            Guid workspaceId,
            [FromBody] CreateInviteRequest req,
            InviteService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            var permCtx = ctx.ToPermissionContext(workspaceId, deviceId);
            try
            {
                return Results.Ok(await svc.CreateAsync(workspaceId, req, permCtx, ct));
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
            catch (InviteConflictException ex)
            {
                // Quem pergunta é o dono do time, sobre o próprio time: aqui a verdade ajuda e não
                // enumera nada que ele já não possa listar em /members.
                return Results.Problem(detail: ex.Message, statusCode: 409,
                    extensions: ctx.ProblemExtensions());
            }
        });

        // ── GET /workspaces/{id}/members ──────────────────────────────────────
        group.MapGet("/members", async (
            Guid workspaceId,
            TeamService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            try
            {
                return Results.Ok(await svc.ListMembersAsync(
                    workspaceId, ctx.ToPermissionContext(workspaceId, deviceId), ct));
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
        });

        // ── DELETE /workspaces/{id}/members/{userId} ──────────────────────────
        group.MapDelete("/members/{targetUserId:guid}", async (
            Guid workspaceId,
            Guid targetUserId,
            TeamService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            try
            {
                var outcome = await svc.RemoveMemberAsync(
                    workspaceId, targetUserId, ctx.ToPermissionContext(workspaceId, deviceId), ct);

                return outcome switch
                {
                    RemoveMemberOutcome.Removed => Results.NoContent(),
                    // 404 e não 204: "removi" para quem nunca esteve lá é mentira na tela.
                    RemoveMemberOutcome.NotAMember => Results.Problem(
                        detail: "Essa pessoa não é membro deste time.",
                        statusCode: 404, extensions: ctx.ProblemExtensions()),
                    _ => Results.Problem(
                        detail: "Este é o último dono do time. Promova outra pessoa a dono antes de "
                                + "remover — sem dono, ninguém mais consegue administrar o time.",
                        statusCode: 409, extensions: ctx.ProblemExtensions()),
                };
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
        });

        // ── GET /workspaces/{id}/key ──────────────────────────────────────────
        group.MapGet("/key", async (
            Guid workspaceId,
            TeamService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            try
            {
                var key = await svc.GetWorkspaceKeyAsync(
                    workspaceId, ctx.ToPermissionContext(workspaceId, deviceId), ct);

                // 404 explícito: workspace sem WK é cofre PESSOAL (raiz AMK, que deriva a chave).
                // O cliente precisa distinguir isso de "erro" para escolher a raiz certa.
                return key is null
                    ? Results.Problem(
                        detail: "Este workspace não tem chave de time guardada para a sua conta.",
                        statusCode: 404, extensions: ctx.ProblemExtensions())
                    : Results.Ok(key);
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
        });

        // ── PUT /workspaces/{id}/key ──────────────────────────────────────────
        //
        // O espelho de escrita do GET acima, e a peça que faltava para o DONO: sem ele, o único
        // caminho que gravava o embrulho era o aceite do convite, e quem criava o time nunca subia
        // o próprio — o segundo computador dele sorteava outra chave e o cofre bifurcava calado.
        group.MapPut("/key", async (
            Guid workspaceId,
            [FromBody] PublishWorkspaceKeyRequest req,
            TeamService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            try
            {
                var outcome = await svc.PublishWorkspaceKeyAsync(
                    workspaceId, req, ctx.ToPermissionContext(workspaceId, deviceId), ct);

                return outcome switch
                {
                    // 200 nos dois casos bons, com `stored` dizendo qual foi: republicar o mesmo
                    // blob é o caminho NORMAL do reparo de boot, e transformá-lo em erro faria o
                    // app gritar todo dia por nada.
                    PublishWorkspaceKeyOutcome.Stored => Results.Ok(
                        new PublishWorkspaceKeyResponse(workspaceId.ToString(), true, req.WkVersion)),
                    PublishWorkspaceKeyOutcome.AlreadyPublished => Results.Ok(
                        new PublishWorkspaceKeyResponse(workspaceId.ToString(), false, req.WkVersion)),

                    // 409, e não 200-ignorando: o servidor mantém o embrulho que já tinha (é ele que
                    // os outros devices deste membro vão restaurar), mas quem publicou PRECISA saber
                    // que a chave dele pode ser outra. Responder "ok" e descartar seria a falha
                    // silenciosa clássica. Quem compara chave de verdade é o cliente, que tem a AMK.
                    _ => Results.Problem(
                        detail: "Já existe uma chave de time guardada para a sua conta neste "
                                + "workspace, e ela é diferente da que este computador enviou. Baixe "
                                + "a chave guardada antes de publicar — trocá-la deixaria os "
                                + "segredos do time ilegíveis nos outros computadores.",
                        statusCode: 409, extensions: ctx.ProblemExtensions()),
                };
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
        });
    }

    private static void MapInviteeScoped(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/invites/{inviteId:guid}").WithTags("Teams").RequireAuthorization();

        // ── POST /invites/{id}/context ────────────────────────────────────────
        group.MapPost("/context", async (
            Guid inviteId,
            [FromBody] InviteContextRequest req,
            InviteService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetUserId(out var userId))
                return Results.Problem("Token sem subject válido.",
                    statusCode: 401, extensions: ctx.ProblemExtensions());

            var context = await svc.GetContextAsync(inviteId, req.CodeHash, userId, ct);
            return context is null
                ? Results.Problem(detail: InviteRefusal, statusCode: 400,
                    extensions: ctx.ProblemExtensions())
                : Results.Ok(context);
        });

        // ── POST /invites/{id}/accept ─────────────────────────────────────────
        group.MapPost("/accept", async (
            Guid inviteId,
            [FromBody] AcceptInviteRequest req,
            InviteService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!ctx.TryGetUserId(out var userId))
                return Results.Problem("Token sem subject válido.",
                    statusCode: 401, extensions: ctx.ProblemExtensions());

            // Material inválido lança ArgumentException → 400 (CloudExceptionHandler). Recusa de
            // convite devolve o MESMO 400 genérico do /context — nenhum dos dois diz o motivo.
            var accepted = await svc.AcceptAsync(inviteId, req, userId, ct);
            return accepted is null
                ? Results.Problem(detail: InviteRefusal, statusCode: 400,
                    extensions: ctx.ProblemExtensions())
                : Results.Ok(accepted);
        });
    }
}
