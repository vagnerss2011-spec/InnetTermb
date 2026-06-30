namespace RemoteOps.Contracts.Assets;

public sealed class Asset
{
    public required string Id { get; init; }

    public required string WorkspaceId { get; init; }

    public string? GroupId { get; init; }

    public required string Name { get; init; }

    public string? Vendor { get; init; }

    public string? Model { get; init; }

    public string? Site { get; init; }

    public List<string> Tags { get; init; } = [];

    public int Version { get; init; }

    public List<Endpoint> Endpoints { get; init; } = [];
}
