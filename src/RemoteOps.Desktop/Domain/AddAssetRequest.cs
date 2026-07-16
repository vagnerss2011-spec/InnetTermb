namespace RemoteOps.Desktop.Domain;

public sealed class AddAssetRequest
{
    public required string WorkspaceId { get; init; }

    public string? GroupId { get; init; }

    public required string Name { get; init; }

    public string? Vendor { get; init; }

    public string? Model { get; init; }

    /// <summary>Papel do device (ver <see cref="RemoteOps.Contracts.Assets.DeviceRoles"/>). null = não classificado.</summary>
    public string? DeviceRole { get; init; }

    public string? Site { get; init; }

    public List<string> Tags { get; init; } = [];
}
