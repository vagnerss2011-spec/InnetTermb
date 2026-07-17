using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RemoteOps.Cloud.Errors;

namespace RemoteOps.Cloud.Auth;

public static class AuthEndpoints
{
    /// <summary>Nome da policy de rate-limit aplicada ao grupo /auth (ver Program.cs).</summary>
    public const string RateLimitPolicy = "auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicy);

        // ── POST /auth/register ───────────────────────────────────────────────
        group.MapPost("/register", async (
            [FromBody] RegisterRequest req,
            AccountService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var result = await svc.RegisterAsync(req, ip, ct);

            // Null = e-mail em uso. 409 sem detalhe: a mensagem não pode virar
            // oráculo de enumeração mais preciso que o /auth/kdf.
            if (result is null)
                return Results.Problem(
                    detail: "Não foi possível concluir o registro.",
                    statusCode: 409,
                    extensions: ctx.ProblemExtensions());

            return Results.Ok(result);
        }).AllowAnonymous();

        // ── GET /auth/kdf ─────────────────────────────────────────────────────
        group.MapGet("/kdf", async (
            [FromQuery] string? email,
            AccountService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(email))
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["email"] = ["Obrigatório."] },
                    extensions: ctx.ProblemExtensions());

            // Sempre 200 com o mesmo shape — conta inexistente recebe params decoy.
            var result = await svc.GetKdfAsync(email, ct);
            return Results.Ok(result);
        }).AllowAnonymous();

        // ── POST /auth/login ──────────────────────────────────────────────────
        group.MapPost("/login", async (
            [FromBody] LoginRequest req,
            TokenService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var hasAuthHash = !string.IsNullOrWhiteSpace(req.AuthHash);
            var hasPassword = !string.IsNullOrWhiteSpace(req.Password);

            if (string.IsNullOrWhiteSpace(req.Email) || hasAuthHash == hasPassword)
                return Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["body"] = ["Informe email e exatamente um entre authHash (E2EE) e password (legado)."],
                    },
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

        // ── POST /auth/password/change ────────────────────────────────────────
        group.MapPost("/password/change", async (
            [FromBody] ChangePasswordRequest req,
            AccountService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                            ?? ctx.User.FindFirstValue("sub");
            if (!Guid.TryParse(userIdStr, out var userId))
                return Results.Problem(
                    detail: "Token sem subject válido.",
                    statusCode: 401,
                    extensions: ctx.ProblemExtensions());

            var ok = await svc.ChangePasswordAsync(userId, req, ct);
            if (!ok)
                return Results.Problem(
                    detail: "AuthHash atual inválido.",
                    statusCode: 401,
                    extensions: ctx.ProblemExtensions());

            return Results.NoContent();
        }).RequireAuthorization();

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
