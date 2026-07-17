using System.Diagnostics;
using System.Threading;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Fase 2, item A: a sessão sincroniza logo APÓS uma edição local (push-ao-mudar, debounced) e faz um
/// flush final no fechamento SEM travar. Testes de tempo com margens folgadas (janela pequena, espera
/// longa) — o padrão já usado na suíte (ver <c>DebouncedActionTests</c>).
/// </summary>
public sealed class SyncSessionPushOnChangeTests
{
    private static SyncChange Change(string id)
        => new() { EntityType = "asset", EntityId = id, Operation = "created", Patch = [] };

    private static (SyncSession Session, FakeCloudSyncApi Api, FakeOutbox Outbox) NewSession(
        TimeSpan? debounce = null, FakeCloudSyncApi? api = null)
    {
        var outbox = new FakeOutbox();
        api ??= new FakeCloudSyncApi();
        var orch = new SyncOrchestrator(
            "ws-1", outbox, api, new FakeRemoteChangeApplier(), new FakeSyncMetadataStore());

        // Intervalo longo + sem StartAsync: o laço por intervalo NÃO roda, então qualquer ciclo
        // observado veio do push-ao-mudar (isola o gatilho sob teste).
        var session = new SyncSession(
            orch, new FakeSyncHintChannel(), "ws-1", TimeSpan.FromHours(1),
            localChanges: outbox, pushDebounce: debounce ?? TimeSpan.FromMilliseconds(60));
        return (session, api, outbox);
    }

    [Fact]
    public async Task Local_Edit_Triggers_A_Debounced_Sync()
    {
        (SyncSession session, FakeCloudSyncApi api, FakeOutbox outbox) = NewSession();
        await using (session)
        {
            await outbox.PushAsync([Change("e1")]);
            await Task.Delay(250);

            Assert.NotEmpty(api.Pushes); // a edição subiu sem esperar o tick
        }
    }

    [Fact]
    public async Task Burst_Of_Edits_Coalesces_Into_One_Cycle()
    {
        (SyncSession session, FakeCloudSyncApi api, FakeOutbox outbox) = NewSession();
        await using (session)
        {
            // Rajada sem espera: os sinais caem na mesma janela → um ciclo só (senão seria N syncs).
            for (int i = 0; i < 6; i++)
            {
                await outbox.PushAsync([Change($"e{i}")]);
            }

            await Task.Delay(250);

            Assert.Single(api.Pulls); // um ciclo de sync agrupou a rajada
        }
    }

    [Fact]
    public async Task No_Edit_Means_No_Sync()
    {
        (SyncSession session, FakeCloudSyncApi api, _) = NewSession();
        await using (session)
        {
            await Task.Delay(150);

            Assert.Empty(api.Pulls); // sem edição e sem Start, nada sincroniza
        }
    }

    [Fact]
    public async Task Flush_Drains_Pending_Outbox_On_Close()
    {
        (SyncSession session, FakeCloudSyncApi api, FakeOutbox outbox) = NewSession();
        await using (session)
        {
            await outbox.PushAsync([Change("e1")]);

            await session.FlushOutboxAsync(TimeSpan.FromSeconds(2));

            Assert.NotEmpty(api.Pushes);
        }
    }

    [Fact]
    public async Task Flush_Does_Not_Hang_When_The_Network_Stalls()
    {
        var api = new FakeCloudSyncApi
        {
            // Rede pendurada: só solta quando o token cancela (o teto do flush).
            PushAsyncHandler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new PushResult("ok", null);
            },
        };
        (SyncSession session, _, FakeOutbox outbox) = NewSession(api: api);
        await using (session)
        {
            await outbox.PushAsync([Change("e1")]);

            var sw = Stopwatch.StartNew();
            await session.FlushOutboxAsync(TimeSpan.FromMilliseconds(100));
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds < 2000, $"flush travou {sw.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public async Task Flush_Offline_Does_Not_Throw()
    {
        var api = new FakeCloudSyncApi
        {
            PushHandler = _ => throw new CloudSyncException(System.Net.HttpStatusCode.ServiceUnavailable),
        };
        (SyncSession session, _, FakeOutbox outbox) = NewSession(api: api);
        await using (session)
        {
            await outbox.PushAsync([Change("e1")]);

            await session.FlushOutboxAsync(TimeSpan.FromSeconds(1)); // não lança
        }
    }
}
