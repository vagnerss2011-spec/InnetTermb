using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncSessionTests
{
    private static SyncOrchestrator Orchestrator(FakeCloudSyncApi api)
        => new("ws-1", new FakeOutbox(), api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

    [Fact]
    public async Task Hint_For_Workspace_Triggers_Sync()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(20));

        await hints.RaiseAsync(new WorkspaceChangedHint("ws-1", 5, "asset", "e1"));

        await WaitUntilAsync(() => api.Pulls.Count > 0);
        Assert.NotEmpty(api.Pulls);
    }

    [Fact]
    public async Task Hint_For_Other_Workspace_Is_Ignored()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(20));

        await hints.RaiseAsync(new WorkspaceChangedHint("ws-OTHER", 5, "asset", "e1"));

        await Task.Delay(100); // tempo de sobra para um sync indevido aparecer
        Assert.Empty(api.Pulls);
    }

    // O id do workspace trafega como string e já houve divergência de grafia entre o caminho env-var
    // (GUID cru) e o broadcast do servidor (formato "D" minúsculo). Comparar com Ordinal descartava o
    // hint legítimo em silêncio — o device só via a novidade no próximo tick do laço.
    [Fact]
    public async Task Hint_With_Different_Case_Is_Accepted()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(20));

        await hints.RaiseAsync(new WorkspaceChangedHint("WS-1", 5, "asset", "e1"));

        await WaitUntilAsync(() => api.Pulls.Count > 0);
        Assert.NotEmpty(api.Pulls);
    }

    // Servidor emite 1 hint por change: importar 200 hosts viraria ~200 ciclos completos enfileirados.
    // A janela de debounce agrupa a rajada num único ciclo.
    [Fact]
    public async Task Hint_Burst_Coalesces_Into_Single_Sync()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(120));

        for (int i = 0; i < 25; i++)
        {
            await hints.RaiseAsync(new WorkspaceChangedHint("ws-1", i, "asset", $"e{i}"));
        }

        await WaitUntilAsync(() => api.Pulls.Count > 0);
        await Task.Delay(250); // deixa a janela fechar de vez
        Assert.Single(api.Pulls);
    }

    [Fact]
    public async Task Start_Connects_The_Hint_Channel()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await session.StartAsync();

        // StartAsync não espera mais o connect (ele roda em fundo, com retry) — daí o WaitUntil.
        await WaitUntilAsync(() => hints.Connected);
        Assert.True(hints.Connected);
    }

    [Fact]
    public async Task Start_Still_Syncs_When_Hint_Connect_Fails()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel { ThrowOnConnect = true };
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await session.StartAsync();

        // O laço por intervalo roda mesmo com o canal de hints indisponível (rede sem WebSocket).
        await WaitUntilAsync(() => api.Pulls.Count > 0);
        Assert.NotEmpty(api.Pulls);
    }

    // O laço é a REDE DE SEGURANÇA do canal de hints: ele não pode morrer por causa de um assinante
    // que lançou. Antes deste teste, uma exceção de StatusChanged (ex.: Dispatcher em shutdown)
    // encerrava o polling em silêncio e o device parava de sincronizar até reiniciar.
    [Fact]
    public async Task Polling_Loop_Survives_Subscriber_Exception()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        SyncOrchestrator orchestrator = Orchestrator(api);
        bool threwOnce = false;
        orchestrator.StatusChanged += _ =>
        {
            if (!threwOnce)
            {
                threwOnce = true;
                throw new InvalidOperationException("assinante quebrado");
            }
        };

        await using var session = new SyncSession(
            orchestrator, hints, "ws-1", TimeSpan.FromMilliseconds(20),
            errorRetry: TimeSpan.FromMilliseconds(10));
        await session.StartAsync();

        // Se o laço tivesse morrido, os pulls parariam no primeiro ciclo.
        await WaitUntilAsync(() => api.Pulls.Count >= 2);
        Assert.True(api.Pulls.Count >= 2);
    }

    // Uma falha no PRIMEIRO connect era engolida e o canal NUNCA mais tentava: o app passava o resto
    // da sessão em polling puro, sem nenhum sinal de que o tempo real tinha morrido. Quem abre o app
    // antes da VPN subir é o caso comum — e a rede volta segundos depois.
    [Fact]
    public async Task Start_Retries_Hint_Connect_After_Initial_Failure()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel { ThrowOnConnect = true };
        await using var session = new SyncSession(Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1));

        await session.StartAsync();

        // A rede volta logo após a primeira tentativa; o canal tem que se reerguer sozinho.
        hints.ThrowOnConnect = false;

        await WaitUntilAsync(() => hints.Connected, timeoutMs: 8000);
        Assert.True(hints.Connected);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("condição não satisfeita no tempo esperado");
    }
}
