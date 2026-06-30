using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RemoteOps.Cloud.Errors;
using RemoteOps.Cloud.Rbac;

namespace RemoteOps.Cloud.Sync;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sync").WithTags("Sync").RequireAuthorization();

        group.MapGet("/pull", async (
            [FromQuery] string workspaceId,
            [FromQuery] long cursor,
            [FromQuery] int pageSize,
            SyncService svc,
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
                var result = await svc.PullAsync(wsId, cursor, pageSize > 0 ? pageSize : 200, permCtx, ct);
                return Results.Ok(result);
            }
            catch (RbacDeniedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 403,
                    extensions: ctx.ProblemExtensions());
            }
        });

        group.MapPost("/push", async (
            [FromBody] PushRequest req,
            SyncService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(req.WorkspaceId, out var wsId))
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["workspaceId"] = ["GUID inválido."] },
                    extensions: ctx.ProblemExtensions());

            if (req.Changes is null || req.Changes.Count == 0)
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["changes"] = ["Lista de changes não pode ser vazia."] },
                    extensions: ctx.ProblemExtensions());

            if (!ctx.TryGetDeviceId(out var deviceId))
                return Results.Problem("X-Device-Id header ausente ou inválido.",
                    statusCode: 400, extensions: ctx.ProblemExtensions());

            var permCtx = ctx.ToPermissionContext(wsId, deviceId);
            try
            {
                var result = await svc.PushAsync(wsId, req.Changes, permCtx, ct);
                return result.Status == "conflict"
                    ? Results.Conflict(result)
                    : Results.Ok(result);
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

internal static class HttpContextExtensions
{
    internal static PermissionContext ToPermissionContext(this HttpContext ctx, Guid workspaceId, Guid deviceId)
    {
        var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? ctx.User.FindFirstValue("sub") ?? string.Empty;
        Guid.TryParse(userIdStr, out var userId);
        return new PermissionContext(userId, workspaceId, deviceId);
    }

    internal static bool TryGetDeviceId(this HttpContext ctx, out Guid deviceId)
    {
        deviceId = default;
        return ctx.Request.Headers.TryGetValue("X-Device-Id", out var devHeader)
               && Guid.TryParse(devHeader, out deviceId);
    }
}
