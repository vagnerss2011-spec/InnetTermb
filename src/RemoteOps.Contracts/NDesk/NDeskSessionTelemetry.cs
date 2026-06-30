namespace RemoteOps.Contracts.NDesk;

public sealed class NDeskSessionTelemetry
{
    public required string SessionId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>direct | turn | relayTcp | unknown.</summary>
    public required string Route { get; init; }

    public double RttMs { get; init; }

    public double? PacketLossPercent { get; init; }

    public double BitrateKbps { get; init; }

    public double? FpsCaptured { get; init; }

    public double FpsDelivered { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? Codec { get; init; }

    public double? AgentCpuPercent { get; init; }

    public double? AgentMemoryMb { get; init; }

    /// <summary>auto | lowBandwidth | balanced | highQuality.</summary>
    public string? QualityProfile { get; init; }
}
