using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Aplica mudanças puxadas do servidor no store local, de forma idempotente e SEM re-emitir
/// no outbox (evita loop de eco). Implementação canônica: <see cref="LocalEntitiesChangeApplier"/>.
/// </summary>
public interface IRemoteChangeApplier
{
    Task ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default);
}
