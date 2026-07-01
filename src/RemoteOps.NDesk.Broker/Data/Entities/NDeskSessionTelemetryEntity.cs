namespace RemoteOps.NDesk.Broker.Data.Entities;

/// <summary>
/// Amostra de telemetria de sessão (docs/22 §Telemetria obrigatória). Nunca contém
/// conteúdo de tela, senha ou dado de entrada do usuário.
/// </summary>
public sealed class NDeskSessionTelemetryEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>direct | turn | relayTcp | unknown.</summary>
    public required string Route { get; set; }

    public double RttMs { get; set; }
    public double? PacketLossPercent { get; set; }
    public double BitrateKbps { get; set; }
    public double? FpsCaptured { get; set; }
    public double FpsDelivered { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? Codec { get; set; }
    public double? AgentCpuPercent { get; set; }
    public double? AgentMemoryMb { get; set; }

    /// <summary>auto | lowBandwidth | balanced | highQuality.</summary>
    public string? QualityProfile { get; set; }
}
