using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

internal sealed class FakeRemoteChangeApplier : IRemoteChangeApplier
{
    public List<SyncChange> Applied { get; } = [];

    /// <summary>
    /// Força quantas "linhas" cada ApplyAsync reporta como gravadas. <c>null</c> = devolve a
    /// contagem de mudanças recebidas (comportamento natural). Serve pra simular o no-op do applier
    /// real (patch de versão antiga) sem precisar de um banco: força 0 mesmo com mudanças no lote.
    /// </summary>
    public int? ForcedAppliedCount { get; set; }

    public Task<int> ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default)
    {
        Applied.AddRange(changes);
        return Task.FromResult(ForcedAppliedCount ?? changes.Count);
    }
}
