namespace RemoteOps.Contracts.Sync;

public sealed class SyncChange
{
    public string? ClientChangeId { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    /// <summary>created | updated | deleted.</summary>
    public required string Operation { get; init; }

    public int BaseVersion { get; init; }

    public required Dictionary<string, object?> Patch { get; init; }
}
