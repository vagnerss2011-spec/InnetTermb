namespace RemoteOps.Cloud.Errors;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    private const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var correlationId = ctx.Request.Headers.TryGetValue(Header, out var existing) && !string.IsNullOrEmpty(existing)
            ? existing.ToString()
            : Guid.NewGuid().ToString();

        ctx.Items[Header] = correlationId;
        ctx.Response.Headers[Header] = correlationId;

        await next(ctx);
    }
}

public static class ProblemExtensionHelpers
{
    /// <summary>
    /// Extensões do ProblemDetails. Sempre inclui o correlationId; pares extras (ex.: um código de
    /// erro estruturado como <c>("error", "mfa_required")</c>) entram como membros de topo do JSON —
    /// é assim que o cliente distingue um 401 de 2FA de um 401 de credencial inválida.
    /// </summary>
    public static Dictionary<string, object?> ProblemExtensions(
        this HttpContext ctx, params (string Key, object? Value)[] extra)
    {
        var correlationId = ctx.Items.TryGetValue("X-Correlation-Id", out var cid)
            ? cid?.ToString()
            : null;
        var result = new Dictionary<string, object?> { ["correlationId"] = correlationId };
        foreach (var (key, value) in extra)
        {
            result[key] = value;
        }

        return result;
    }
}
