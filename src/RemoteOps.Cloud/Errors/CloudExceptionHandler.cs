using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Errors;

public sealed class CloudExceptionHandler(ILogger<CloudExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx,
        Exception exception,
        CancellationToken ct)
    {
        var correlationId = ctx.Items.TryGetValue("X-Correlation-Id", out var cid)
            ? cid?.ToString()
            : ctx.TraceIdentifier;

        var (status, title) = exception switch
        {
            RbacDeniedException => (403, "Acesso negado"),
            ArgumentException or FormatException => (400, "Requisição inválida"),
            _ => (500, "Erro interno"),
        };

        if (status == 500)
            logger.LogError(exception, "Unhandled exception correlationId={CorrelationId}", correlationId);
        else
            logger.LogWarning("Handled exception {Type} correlationId={CorrelationId}: {Message}",
                exception.GetType().Name, correlationId, exception.Message);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status < 500 ? exception.Message : "Contate o suporte com o correlationId.",
            Extensions = { ["correlationId"] = correlationId },
        };

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
