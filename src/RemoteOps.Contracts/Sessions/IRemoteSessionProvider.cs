namespace RemoteOps.Contracts.Sessions;

public interface IRemoteSessionProvider
{
    string Protocol { get; }

    Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct);

    Task CloseAsync(SessionHandle handle, CancellationToken ct);
}
