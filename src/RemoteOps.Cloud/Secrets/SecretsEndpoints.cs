using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Errors;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Secrets;

public static class SecretsEndpoints
{
    public static IEndpointRouteBuilder MapSecretsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/secrets").WithTags("Secrets").RequireAuthorization();

        // ── POST /secrets ─────────────────────────────────────────────────────
        group.MapPost("/", async (
            [FromBody] SecretsUpsertRequest req,
            SecretsService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(req.WorkspaceId, out var wsId))
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["workspaceId"] = ["GUID inválido."] },
                    extensions: ctx.ProblemExtensions());

            if (req.Envelope is null)
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["envelope"] = ["Obrigatório."] },
                    extensions: ctx.ProblemExtensions());

            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            var permCtx = ctx.ToPermissionContext(wsId, deviceId);
            try
            {
                var result = await svc.UpsertAsync(wsId, req.Envelope, permCtx, ct);
                return result.Status == "conflict"
                    ? Results.Conflict(result)
                    : Results.Ok(result);
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
            catch (DbUpdateException)
            {
                // Corrida no cursor do workspace (índice único WorkspaceId+Cursor).
                // 409 é a resposta certa: o outbox do cliente re-tenta e pega o
                // próximo cursor. Ver SecretsService.NextCursorAsync.
                return Results.Conflict(new SecretUpsertResult(
                    "conflict", 0, null, "cursor.race-retry"));
            }
        });

        // ── GET /secrets?workspaceId=&since= ──────────────────────────────────
        // since/pageSize são NULÁVEIS de propósito: em minimal API um value type
        // não-nulável vindo de query é OBRIGATÓRIO, e omitir viraria erro de request.
        // O device novo chama sem cursor (baixa o cofre inteiro).
        group.MapGet("/", async (
            [FromQuery] string workspaceId,
            [FromQuery] long? since,
            [FromQuery] int? pageSize,
            SecretsService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(workspaceId, out var wsId))
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["workspaceId"] = ["GUID inválido."] },
                    extensions: ctx.ProblemExtensions());

            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            var permCtx = ctx.ToPermissionContext(wsId, deviceId);
            try
            {
                var result = await svc.PullAsync(
                    wsId, since ?? 0, pageSize is > 0 ? pageSize.Value : 200, permCtx, ct);
                return Results.Ok(result);
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
        });

        return app;
    }
}
