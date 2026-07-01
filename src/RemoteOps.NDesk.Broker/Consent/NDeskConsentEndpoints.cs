using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RemoteOps.NDesk.Broker;

namespace RemoteOps.NDesk.Broker.Consent;

public sealed record GrantConsentBody(
    string GrantedByDisplayName,
    string? GrantedByWindowsUser,
    string GrantedByMachineName,
    string Mode,
    List<string> Permissions,
    int? TtlSeconds,
    string? ConsentTextVersion);

public sealed record DenyConsentBody(string? Reason);

public static class NDeskConsentEndpoints
{
    public static IEndpointRouteBuilder MapNDeskConsentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ndesk/sessions/{sessionId}").WithTags("NDesk Consent");

        // O usuário assistido (anônimo) concede consentimento explícito antes de qualquer signaling.
        group.MapPost("/consent", async (
            string sessionId,
            [FromBody] GrantConsentBody body,
            NDeskPermissionGrantService svc,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sid))
                return Results.NotFound();

            if (!NDeskEnums.Modes.Contains(body.Mode))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mode"] = [$"Valor inválido. Use: {string.Join(", ", NDeskEnums.Modes)}."],
                });

            if (body.Permissions.Exists(p => !NDeskEnums.Permissions.Contains(p)))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["permissions"] = [$"Valores inválidos. Use: {string.Join(", ", NDeskEnums.Permissions)}."],
                });

            var result = await svc.GrantConsentAsync(new GrantConsentRequest(
                SessionId: sid,
                GrantedBy: new GrantedBy(body.GrantedByDisplayName, body.GrantedByWindowsUser, body.GrantedByMachineName),
                Mode: body.Mode,
                Permissions: body.Permissions,
                Ttl: body.TtlSeconds is > 0 ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : null,
                ConsentTextVersion: body.ConsentTextVersion), ct);

            return result.Outcome switch
            {
                GrantOutcome.Granted => Results.Ok(result.Grant),
                GrantOutcome.NoActiveTicket => Results.Problem(detail: "Sessão sem convite ativo.", statusCode: 409),
                GrantOutcome.PermissionsExceedRequest =>
                    Results.Problem(detail: "Consentimento excede o que foi solicitado no convite.", statusCode: 422),
                _ => Results.Problem(statusCode: 500),
            };
        }).AllowAnonymous();

        group.MapPost("/consent/deny", async (
            string sessionId,
            [FromBody] DenyConsentBody body,
            NDeskPermissionGrantService svc,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sid))
                return Results.NotFound();

            await svc.DenyConsentAsync(sid, body.Reason, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        // Revogação imediata (CLAUDE.md princípio 3) — chamável por qualquer lado da sessão.
        // O agente (usuário assistido) é anônimo por design; só possui o sessionId (mesmo nível
        // de confiança já aceito em NDeskSignalingHub.EndSession). Por isso este endpoint não
        // exige autenticação — mas, precisamente por não poder verificar identidade, NUNCA confia
        // num "revokedBy" vindo do corpo da requisição (permitiria forjar o autor no log de
        // auditoria); o autor é sempre derivado do contexto, nunca de input do cliente.
        group.MapPost("/revoke", async (
            string sessionId,
            NDeskPermissionGrantService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sid))
                return Results.NotFound();

            var revokedBy = ctx.User.Identity?.IsAuthenticated == true
                ? (ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? "operator")
                : "assisted-user";

            await svc.RevokeConsentAsync(sid, revokedBy, ct);
            return Results.NoContent();
        }).AllowAnonymous();

        return app;
    }
}
