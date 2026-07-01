using RemoteOps.NDesk.Broker;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Data.Entities;

namespace RemoteOps.NDesk.Broker.Telemetry;

public sealed record RecordTelemetryRequest(
    Guid SessionId,
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

/// <summary>
/// Telemetria de sessão (contracts/ndesk-session-telemetry.schema.json, docs/22 §Telemetria
/// obrigatória). Nunca aceita/persiste conteúdo de tela, senha ou payload de input.
/// </summary>
public sealed class NDeskTelemetryService(NDeskDbContext db, TimeProvider clock)
{
    public async Task<bool> RecordAsync(RecordTelemetryRequest req, CancellationToken ct = default)
    {
        if (!NDeskEnums.Routes.Contains(req.Route)) return false;
        if (req.QualityProfile is not null && !NDeskEnums.QualityProfiles.Contains(req.QualityProfile)) return false;

        db.Telemetry.Add(new NDeskSessionTelemetryEntity
        {
            Id = Guid.NewGuid(),
            SessionId = req.SessionId,
            Timestamp = clock.GetUtcNow(),
            Route = req.Route,
            RttMs = req.RttMs,
            PacketLossPercent = req.PacketLossPercent,
            BitrateKbps = req.BitrateKbps,
            FpsCaptured = req.FpsCaptured,
            FpsDelivered = req.FpsDelivered,
            Width = req.Width,
            Height = req.Height,
            Codec = req.Codec,
            AgentCpuPercent = req.AgentCpuPercent,
            AgentMemoryMb = req.AgentMemoryMb,
            QualityProfile = req.QualityProfile,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
