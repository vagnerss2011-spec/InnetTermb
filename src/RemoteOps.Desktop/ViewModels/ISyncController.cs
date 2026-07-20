using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// O que a UI precisa pra FORÇAR um sync (Fase 2, item B): abstrai o orquestrador pra a VM não
/// depender de <c>RemoteOps.Sync.Remote</c> nem de rede — testável com um dublê. "Sincronizar agora"
/// = um ciclo push+pull (o <c>SyncOnceAsync</c>), serializado com o laço de fundo pelo gate do
/// orquestrador; forçar subir e forçar baixar são as duas metades desse mesmo ciclo.
/// </summary>
public interface ISyncController
{
    Task SyncNowAsync(CancellationToken ct = default);

    /// <summary>Conflitos registrados, do mais recente para o mais antigo — para a UI EXPLICAR.</summary>
    Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default);

    /// <summary>Dispensa os conflitos ("já vi"). Não desliga a detecção: conflito novo volta a aparecer.</summary>
    Task DismissConflictsAsync(CancellationToken ct = default);
}
