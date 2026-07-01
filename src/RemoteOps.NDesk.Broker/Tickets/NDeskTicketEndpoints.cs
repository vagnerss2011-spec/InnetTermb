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

            var userIdStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub");
            Guid.TryParse(userIdStr, out var userId);

            var ticket = await svc.IssueTicketAsync(new IssueTicketRequest(
                WorkspaceId: workspaceId,
                CreatedByUserId: userId == Guid.Empty ? null : userId,
                RequestedMode: body.RequestedMode,
                PermissionsRequested: body.PermissionsRequested,
                Ttl: body.TtlSeconds is > 0 ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : null,
                AgentMinimumWindows: body.AgentMinimumWindows,
                AgentAllowWindows7Legacy: body.AgentAllowWindows7Legacy,
                AgentRequiresInstall: body.AgentRequiresInstall), ct);

            return Results.Ok(ticket);
        }).RequireAuthorization();

        // Status do convite — nunca inclui o link token (só sai uma vez, na emissão).
        group.MapGet("/{id}", async (string id, NDeskTicketService svc, CancellationToken ct) =>
        {
            if (!Guid.TryParse(id, out var ticketId))
                return Results.NotFound();

            var ticket = await svc.GetStatusAsync(ticketId, ct);
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
