using RemoteOps.Contracts.Sessions;
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Desktop.Rdp.Fakes;

internal sealed class FakeRdpSessionProvider : IRdpSessionProvider
{
    public string Protocol => RemoteProtocol.Rdp;
    public List<SessionRequest> OpenedRequests { get; } = [];
    public int CloseCount { get; private set; }
    public bool ShouldThrowOnOpen { get; set; }
    public RdpConnectionConfig ConfigToReturn { get; set; } = new()
    {
        Host = "10.0.0.5",
        Port = 3389,
        Username = "admin",
        NlaRequired = true,
        Redirection = RdpRedirectionPolicy.Default,
    };

    public Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        if (ShouldThrowOnOpen) throw new InvalidOperationException("Fake provider: open failed");

        OpenedRequests.Add(request);
        return Task.FromResult(new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = request.Protocol,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        });
    }

    public RdpConnectionConfig GetConnectionConfig(string sessionId) => ConfigToReturn;

    public Task CloseAsync(SessionHandle handle, CancellationToken ct)
    {
        CloseCount++;
        return Task.CompletedTask;
    }
}
