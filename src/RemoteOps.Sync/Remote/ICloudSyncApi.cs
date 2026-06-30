using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Cliente HTTP do backend de sync (ADR-010/ADR-013). Toda chamada autenticada envia
/// <c>Authorization: Bearer</c> + <c>X-Device-Id</c> e faz refresh automático em 401.
/// </summary>
public interface ICloudSyncApi
{
    Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default);

    Task<PullResponse> PullAsync(string workspaceId, long cursor, int pageSize, CancellationToken ct = default);
}
