using Microsoft.AspNetCore.Mvc;
using RemoteOps.Cloud.Errors;

namespace RemoteOps.Cloud.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (
            [FromBody] LoginRequest req,
            TokenService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["body"] = ["Email e password são obrigatórios."] },
                    extensions: ctx.ProblemExtensions());

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await svc.LoginAsync(req, ip, ct);

            if (result is null)
                return Results.Problem(
                    detail: "Credenciais inválidas ou dispositivo revogado.",
                    statusCode: 401,
                    extensions: ctx.ProblemExtensions());

            return Results.Ok(result);
        }).AllowAnonymous();

        group.MapPost("/refresh", async (
            [FromBody] RefreshRequest req,
            TokenService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await svc.RefreshAsync(req, ip, ct);

            if (result is null)
                return Results.Problem(
                    detail: "Refresh token inválido ou expirado.",
                    statusCode: 401,
                    extensions: ctx.ProblemExtensions());

            return Results.Ok(result);
        }).AllowAnonymous();

        group.MapPost("/logout", async (
            [FromBody] LogoutRequest req,
            TokenService svc,
            CancellationToken ct) =>
        {
            await svc.LogoutAsync(req.RefreshToken, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }
}

public sealed record LogoutRequest(string RefreshToken);
