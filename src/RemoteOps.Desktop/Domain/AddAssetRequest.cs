namespace RemoteOps.Desktop.Domain;

public sealed class AddAssetRequest
{
    public required string WorkspaceId { get; init; }

    public string? GroupId { get; init; }

    public required string Name { get; init; }

    public string? Vendor { get; init; }

    public string? Model { get; init; }

    public string? Site { get; init; }

    public List<string> Tags { get; init; } = [];
}
