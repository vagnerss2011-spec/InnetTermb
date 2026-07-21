using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Liga o <see cref="ISyncController"/> da UI ao <see cref="SyncOrchestrator"/> real (Fase 2, item B).
/// "Sincronizar agora" é um <see cref="SyncOrchestrator.SyncOnceAsync"/> — push+pull num ciclo só,
/// já serializado com o laço de fundo pelo gate do orquestrador (chamadas concorrentes esperam, nunca
/// rodam em paralelo).
/// </summary>
public sealed class OrchestratorSyncController : ISyncController
{
    private readonly SyncOrchestrator _orchestrator;

    public OrchestratorSyncController(SyncOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    // O orquestrador engole a falha do ciclo (offline-first) e a devolve como estado; aqui ela vira
    // o bool do contrato — é o retorno do PRÓPRIO ciclo, não uma leitura de Status depois do await
    // (que seria corrida com o próximo ciclo do laço de fundo).
    public async Task<bool> SyncNowAsync(CancellationToken ct = default)
    {
        SyncStatus status = await _orchestrator.SyncOnceAsync(ct);
        return status.State == SyncState.Synced;
    }

    // A tradução para o tipo da UI acontece AQUI, na fronteira: é o que mantém a VM sem dependência
    // de RemoteOps.Sync.Remote (mesmo racional do ISyncController).
    public async Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(
        int limit, CancellationToken ct = default)
    {
        IReadOnlyList<StoredConflict> stored = await _orchestrator.GetConflictsAsync(limit, ct);
        return stored
            .Select(c => new SyncConflictItem(c.EntityType, c.EntityId, c.DetectedAt, c.Reason))
            .ToList();
    }

    public Task DismissConflictsAsync(CancellationToken ct = default)
        => _orchestrator.ClearConflictsAsync(ct);
}
