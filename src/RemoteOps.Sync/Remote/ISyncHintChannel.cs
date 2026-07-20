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

    /// <summary>
    /// O canal está entregando hints em tempo real? <c>false</c> = caiu para o laço por intervalo.
    /// Existe para o operador diagnosticar em campo uma rede que bloqueia WebSocket sem depender de
    /// log — e nenhum log poderia ajudar aqui, já que a URL do hub carrega o JWT e não pode ser
    /// impressa (ADR-013).
    /// </summary>
    bool IsRealTime { get; }

    /// <summary>Disparado quando <see cref="IsRealTime"/> muda.</summary>
    event Action<bool>? RealTimeChanged;

    Task ConnectAsync(string workspaceId, CancellationToken ct = default);
}
