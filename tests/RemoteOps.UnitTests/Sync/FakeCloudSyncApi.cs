using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>Dublê de <see cref="ICloudSyncApi"/>: registra requests e devolve respostas roteirizadas.</summary>
internal sealed class FakeCloudSyncApi : ICloudSyncApi
{
    public List<PushRequest> Pushes { get; } = [];

    public Queue<PushResult> PushResults { get; } = new();

    public Func<PushRequest, PushResult>? PushHandler { get; set; }

    /// <summary>
    /// Handler ASSÍNCRONO do push, pra simular uma rede que PENDURA (respeitando o token de cancelamento):
    /// usado pelo teste do flush no fechamento pra provar que o timeout não trava. Tem precedência.
    /// </summary>
    public Func<PushRequest, CancellationToken, Task<PushResult>>? PushAsyncHandler { get; set; }

    public List<(string Workspace, long Cursor, int PageSize)> Pulls { get; } = [];

    public Queue<PullResponse> PullResponses { get; } = new();

    public async Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
    {
        Pushes.Add(request);

        if (PushAsyncHandler is not null)
        {
            return await PushAsyncHandler(request, ct);
        }

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

        return result;
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
