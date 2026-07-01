using Microsoft.AspNetCore.Mvc;

namespace RemoteOps.NDesk.Broker.Telemetry;

public sealed record TelemetrySampleBody(
    string Route,
    double RttMs,
    double? PacketLossPercent,
    double BitrateKbps,
    double? FpsCaptured,
    double FpsDelivered,
    int? Width,
    int? Height,
    string? Codec,
    double? AgentCpuPercent,
    double? AgentMemoryMb,
    string? QualityProfile);

public static class NDeskTelemetryEndpoints
{
    public static IEndpointRouteBuilder MapNDeskTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ndesk/sessions/{sessionId}/telemetry", async (
            string sessionId,
            [FromBody] TelemetrySampleBody body,
            NDeskTelemetryService svc,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(sessionId, out var sid))
                return Results.NotFound();

            var ok = await svc.RecordAsync(new RecordTelemetryRequest(
                SessionId: sid,
                Route: body.Route,
                RttMs: body.RttMs,
                PacketLossPercent: body.PacketLossPercent,
                BitrateKbps: body.BitrateKbps,
                FpsCaptured: body.FpsCaptured,
                FpsDelivered: body.FpsDelivered,
                Width: body.Width,
                Height: body.Height,
                Codec: body.Codec,
                AgentCpuPercent: body.AgentCpuPercent,
                AgentMemoryMb: body.AgentMemoryMb,
                QualityProfile: body.QualityProfile), ct);

            return ok ? Results.NoContent() : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["route/qualityProfile"] = ["Valor de enum inválido."],
            });
        }).WithTags("NDesk Telemetry");

        return app;
    }
}
