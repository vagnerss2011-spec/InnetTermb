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

    public Task SyncNowAsync(CancellationToken ct = default) => _orchestrator.SyncOnceAsync(ct);
}
