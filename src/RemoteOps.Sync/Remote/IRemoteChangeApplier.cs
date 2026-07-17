using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Aplica mudanças puxadas do servidor no store local, de forma idempotente e SEM re-emitir
/// no outbox (evita loop de eco). Implementação canônica: <see cref="LocalEntitiesChangeApplier"/>.
/// </summary>
public interface IRemoteChangeApplier
{
    /// <summary>
    /// Materializa o lote nas tabelas locais e devolve QUANTAS linhas das tabelas que a UI lê foram
    /// realmente gravadas (insert/update/delete efetivo). Zero = re-aplicação idempotente/no-op ou
    /// tipo desconhecido em quarentena — nada que o operador enxergue mudou. É esse número que deixa
    /// o <see cref="SyncOrchestrator"/> avisar a UI só quando há de fato o que recarregar (Fase 2).
    /// </summary>
    Task<int> ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default);
}
