namespace RemoteOps.Desktop.Domain;

public sealed class AssetGroup
{
    public required string Id { get; init; }

    public required string WorkspaceId { get; init; }

    public string? ParentId { get; init; }

    public required string Name { get; set; }

    public string? DefaultCredentialRefId { get; set; }

    public int Version { get; init; }
}
