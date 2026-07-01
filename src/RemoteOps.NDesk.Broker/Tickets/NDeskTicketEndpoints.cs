using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RemoteOps.NDesk.Broker;

namespace RemoteOps.NDesk.Broker.Tickets;

public sealed record IssueTicketBody(
    string WorkspaceId,
    string? RequestedMode,
    List<string>? PermissionsRequested,
    int? TtlSeconds,
    string? AgentMinimumWindows,
    bool AgentAllowWindows7Legacy,
    bool AgentRequiresInstall);

public sealed record RedeemTicketBody(string LinkToken);

public static class NDeskTicketEndpoints
{
    public static IEndpointRouteBuilder MapNDeskTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ndesk/tickets").WithTags("NDesk Tickets");

        // Operador autenticado emite o convite. O link token só existe nesta resposta.
        group.MapPost("/", async (
            [FromBody] IssueTicketBody body,
            NDeskTicketService svc,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(body.WorkspaceId, out var workspaceId))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["workspaceId"] = ["GUID inválido."],
                });

            if (body.RequestedMode is not null && !NDeskEnums.Modes.Contains(body.RequestedMode))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["requestedMode"] = [$"Valor inválido. Use: {string.Join(", ", NDeskEnums.Modes)}."],
                });

            if (body.PermissionsRequested?.Exists(p => !NDeskEnums.Permissions.Contains(p)) == true)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["permissionsRequested"] = [$"Valores inválidos. Use: {string.Join(", ", NDeskEnums.Permissions)}."],
                });

            if (!ctx.TryGetCallerUserId(out var userId))
                return Results.Problem(detail: "Token sem claim de usuário válida.", statusCode: 401);

            // NOTA DE SEGURANÇA (ver ADR-018 §Consequências negativas): o broker não valida que
            // userId é de fato membro de workspaceId — não há tabela de Memberships aqui (fica em
            // RemoteOps.Cloud, propositalmente não referenciado para não acoplar os módulos). Um
            // operador autenticado pode declarar qualquer workspaceId. O blast radius é limitado
            // porque (a) GetStatusAsync já escopa consulta ao criador do ticket — ver abaixo — e
            // (b) o controle de segurança real (consentimento explícito do lado assistido)
            // independe do workspaceId declarado. Ainda assim, é um débito rastreado, não uma
            // aceitação silenciosa — bloqueante antes de qualquer UI que confie em listagem de
            // tickets por workspace.
            var ticket = await svc.IssueTicketAsync(new IssueTicketRequest(
                WorkspaceId: workspaceId,
                CreatedByUserId: userId,
                RequestedMode: body.RequestedMode,
                PermissionsRequested: body.PermissionsRequested,
                Ttl: body.TtlSeconds is > 0 ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : null,
                AgentMinimumWindows: body.AgentMinimumWindows,
                AgentAllowWindows7Legacy: body.AgentAllowWindows7Legacy,
                AgentRequiresInstall: body.AgentRequiresInstall), ct);

            return Results.Ok(ticket);
        }).RequireAuthorization();

        // Status do convite — escopado ao operador que o criou (evita IDOR por enumeração de
        // GUID) e nunca inclui o link token (só sai uma vez, na emissão).
        group.MapGet("/{id}", async (string id, NDeskTicketService svc, HttpContext ctx, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var ticketId))
                return Results.NotFound();

            if (!ctx.TryGetCallerUserId(out var userId))
                return Results.Problem(detail: "Token sem claim de usuário válida.", statusCode: 401);

            var ticket = await svc.GetStatusAsync(ticketId, userId, ct);
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).RequireAuthorization();

        // O agente (usuário assistido) é anônimo — só possui o link token recebido fora de banda.
        group.MapPost("/redeem", async (
            [FromBody] RedeemTicketBody body,
            NDeskTicketService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.LinkToken))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["linkToken"] = ["Obrigatório."],
                });

            var result = await svc.RedeemTicketAsync(body.LinkToken, ct);
            return result.Outcome switch
            {
                RedeemOutcome.Success => Results.Ok(new { sessionId = result.SessionId, ticket = result.Ticket }),
                RedeemOutcome.NotFound => Results.NotFound(),
                RedeemOutcome.Expired => Results.Problem(detail: "Convite expirado.", statusCode: 410),
                RedeemOutcome.AlreadyUsed => Results.Problem(detail: "Convite já utilizado.", statusCode: 409),
                _ => Results.Problem(statusCode: 500),
            };
        }).AllowAnonymous();

        return app;
    }
}

internal static class HttpContextExtensions
{
    internal static bool TryGetCallerUserId(this HttpContext ctx, out Guid userId)
    {
        var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub");
        return Guid.TryParse(userIdStr, out userId) && userId != Guid.Empty;
    }
}
