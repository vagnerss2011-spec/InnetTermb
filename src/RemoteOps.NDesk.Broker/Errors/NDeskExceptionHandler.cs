using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace RemoteOps.NDesk.Broker.Errors;

public sealed class NDeskExceptionHandler(ILogger<NDeskExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception exception, CancellationToken ct)
    {
        var correlationId = ctx.TraceIdentifier;

        var (status, title) = exception switch
        {
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
