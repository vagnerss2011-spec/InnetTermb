namespace RemoteOps.Sync.Remote;

/// <summary>
/// Hint de mudança recebido do servidor via SignalR (<c>workspace.changed</c>). É apenas uma
/// dica — sem dados sensíveis — para acionar um pull incremental (ADR-002/ADR-013).
/// </summary>
public sealed record WorkspaceChangedHint(string WorkspaceId, long Cursor, string EntityType, string EntityId);

/// <summary>
/// Canal de hints em tempo real. A implementação concreta (<see cref="SignalRSyncHintChannel"/>)
/// conecta ao hub <c>/hubs/sync</c>, chama <c>JoinWorkspace</c> e levanta <see cref="WorkspaceChanged"/>.
/// </summary>
public interface ISyncHintChannel : IAsyncDisposable
{
    event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    Task ConnectAsync(string workspaceId, CancellationToken ct = default);
}
