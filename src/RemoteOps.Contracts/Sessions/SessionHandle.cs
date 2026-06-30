namespace RemoteOps.Contracts.Sessions;

public sealed class SessionHandle
{
    public required string SessionId { get; init; }

    public required string Protocol { get; init; }

    public required string EndpointId { get; init; }

    public required DateTimeOffset OpenedAt { get; init; }

    public bool IsOpen { get; set; }
}
