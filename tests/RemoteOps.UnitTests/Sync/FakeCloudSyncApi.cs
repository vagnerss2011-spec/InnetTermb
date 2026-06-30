using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>Dublê de <see cref="ICloudSyncApi"/>: registra requests e devolve respostas roteirizadas.</summary>
internal sealed class FakeCloudSyncApi : ICloudSyncApi
{
    public List<PushRequest> Pushes { get; } = [];

    public Queue<PushResult> PushResults { get; } = new();

    public Func<PushRequest, PushResult>? PushHandler { get; set; }

    public List<(string Workspace, long Cursor, int PageSize)> Pulls { get; } = [];

    public Queue<PullResponse> PullResponses { get; } = new();

    public Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
    {
        Pushes.Add(request);

        PushResult result;
        if (PushHandler is not null)
        {
            result = PushHandler(request);
        }
        else if (PushResults.Count > 0)
        {
            result = PushResults.Dequeue();
        }
        else
        {
            result = new PushResult("ok", null);
        }

        return Task.FromResult(result);
    }

    public Task<PullResponse> PullAsync(
        string workspaceId, long cursor, int pageSize, CancellationToken ct = default)
    {
        Pulls.Add((workspaceId, cursor, pageSize));
        PullResponse response = PullResponses.Count > 0
            ? PullResponses.Dequeue()
            : new PullResponse([], cursor, false);
        return Task.FromResult(response);
    }
}
