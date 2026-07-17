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
}
