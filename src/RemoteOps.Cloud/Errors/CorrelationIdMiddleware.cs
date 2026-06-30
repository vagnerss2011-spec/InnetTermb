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
    public static Dictionary<string, object?> ProblemExtensions(this HttpContext ctx)
    {
        var correlationId = ctx.Items.TryGetValue("X-Correlation-Id", out var cid)
            ? cid?.ToString()
            : null;
        return new Dictionary<string, object?> { ["correlationId"] = correlationId };
    }
}
