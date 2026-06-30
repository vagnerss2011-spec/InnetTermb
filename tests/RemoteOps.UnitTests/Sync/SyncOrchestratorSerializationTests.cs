using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Garante que <see cref="SyncOrchestrator.SyncOnceAsync"/> é serializado: o laço por intervalo e o
/// callback de hint compartilham o mesmo outbox e os mesmos cursores; dois ciclos concorrentes
/// corromperiam os cursores (perda de mudanças locais / regressão do server cursor). Ver ADR-013.
/// </summary>
public sealed class SyncOrchestratorSerializationTests
{
    // Dublê de ICloudSyncApi que mede a concorrência máxima observada dentro de PullAsync.
    private sealed class ConcurrencyProbeApi : ICloudSyncApi
    {
        private int _active;
        public int MaxConcurrent { get; private set; }
        public int PullCount { get; private set; }

        public Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
            => Task.FromResult(new PushResult("ok", null));

        public async Task<PullResponse> PullAsync(
            string workspaceId, long cursor, int pageSize, CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref _active);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            PullCount++;
            try
            {
                // Segura a "janela" aberta para que, SEM serialização, um segundo ciclo entre aqui.
                await Task.Delay(30, ct);
                return new PullResponse([], cursor, false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    [Fact]
    public async Task SyncOnceAsync_Is_Serialized_Across_Concurrent_Callers()
    {
        var api = new ConcurrencyProbeApi();
        var orchestrator = new SyncOrchestrator(
            "ws-1", new FakeOutbox(), api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        // Dispara dois ciclos concorrentes (imita laço + hint chegando ao mesmo tempo).
        Task a = orchestrator.SyncOnceAsync();
        Task b = orchestrator.SyncOnceAsync();
        await Task.WhenAll(a, b);

        Assert.Equal(2, api.PullCount);       // ambos os ciclos rodaram...
        Assert.Equal(1, api.MaxConcurrent);   // ...mas nunca em paralelo (gate de exclusão mútua).
    }
}
