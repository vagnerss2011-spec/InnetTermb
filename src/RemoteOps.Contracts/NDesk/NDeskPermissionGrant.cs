namespace RemoteOps.Contracts.NDesk;

public sealed class NDeskPermissionGrant
{
    public required string SessionId { get; init; }

    public required string TicketId { get; init; }

    public required NDeskGrantedBy GrantedBy { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>basic | control | file | administrator.</summary>
    public required string Mode { get; init; }

    public List<string> Permissions { get; init; } = [];

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public string? RevokedBy { get; init; }

    public string? ConsentTextVersion { get; init; }
}

public sealed class NDeskGrantedBy
{
    public required string DisplayName { get; init; }

    public string? WindowsUser { get; init; }

    public required string MachineName { get; init; }
}
