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

            return result.Outcome switch
            {
                LoginOutcome.Success => Results.Ok(result.Response),

                // 2FA: 401 com corpo ESTRUTURADO (error=mfa_required) para a UI saber pedir o código.
                // É distinto do 401 de credencial inválida, mas só chega aqui quem JÁ provou a senha
                // (ver TokenService) — não vira oráculo de enumeração.
                LoginOutcome.MfaRequired => Results.Problem(
                    detail: "Informe o código de verificação em duas etapas do seu aplicativo autenticador.",
                    statusCode: 401,
                    extensions: ctx.ProblemExtensions(("error", "mfa_required"))),

                _ => Results.Problem(
                    detail: "Credenciais inválidas ou dispositivo revogado.",
                    statusCode: 401,
                    extensions: ctx.ProblemExtensions()),
            };
        }).AllowAnonymous();

        MapMfaEndpoints(group);

        // ── POST /auth/password/change ────────────────────────────────────────
        group.MapPost("/password/change", async (
            [FromBody] ChangePasswordRequest req,
            AccountService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (ResolveUserId(ctx) is not { } userId)
                return UnauthorizedSubject(ctx);

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

    /// <summary>
    /// 2FA/TOTP (spec Fase 3). Todos AUTENTICADOS: o userId vem do JWT (nunca do corpo). Enroll gera
    /// o segredo (não ativa); confirm ativa com um código válido; disable desliga exigindo código.
    /// </summary>
    private static void MapMfaEndpoints(RouteGroupBuilder group)
    {
        // ── POST /auth/mfa/enroll ─────────────────────────────────────────────
        group.MapPost("/mfa/enroll", async (
            MfaService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (ResolveUserId(ctx) is not { } userId)
                return UnauthorizedSubject(ctx);

            var result = await svc.EnrollAsync(userId, ct);

            // Null = 2FA já ativo (tem que desativar antes de re-inscrever). 409 sem detalhe sensível.
            if (result is null)
                return Results.Problem(
                    detail: "Não foi possível iniciar a verificação em duas etapas. Ela já pode estar ativa.",
                    statusCode: 409,
                    extensions: ctx.ProblemExtensions());

            return Results.Ok(result);
        }).RequireAuthorization();

        // ── POST /auth/mfa/confirm ────────────────────────────────────────────
        group.MapPost("/mfa/confirm", async (
            [FromBody] MfaConfirmRequest req,
            MfaService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (ResolveUserId(ctx) is not { } userId)
                return UnauthorizedSubject(ctx);

            var ok = await svc.ConfirmAsync(userId, req, ct);
            if (!ok)
                return Results.Problem(
                    detail: "Código inválido. Verifique o app autenticador e tente de novo.",
                    statusCode: 400,
                    extensions: ctx.ProblemExtensions());

            return Results.NoContent();
        }).RequireAuthorization();

        // ── POST /auth/mfa/disable ────────────────────────────────────────────
        group.MapPost("/mfa/disable", async (
            [FromBody] MfaDisableRequest req,
            MfaService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (ResolveUserId(ctx) is not { } userId)
                return UnauthorizedSubject(ctx);

            var ok = await svc.DisableAsync(userId, req, ct);
            if (!ok)
                return Results.Problem(
                    detail: "Código inválido. Informe um código válido para desativar a verificação em duas etapas.",
                    statusCode: 400,
                    extensions: ctx.ProblemExtensions());

            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static Guid? ResolveUserId(HttpContext ctx)
    {
        var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? ctx.User.FindFirstValue("sub");
        return Guid.TryParse(userIdStr, out var userId) ? userId : null;
    }

    private static IResult UnauthorizedSubject(HttpContext ctx)
        => Results.Problem(
            detail: "Token sem subject válido.",
            statusCode: 401,
            extensions: ctx.ProblemExtensions());
}

public sealed record LogoutRequest(string RefreshToken);
