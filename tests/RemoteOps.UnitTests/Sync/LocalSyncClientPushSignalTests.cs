using RemoteOps.Contracts.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Fase 2, item A ("push-ao-mudar"): gravar uma edição no outbox precisa AVISAR quem sincroniza, pra
/// um edit local subir logo — não só no próximo tick. Estes testes fixam o sinal
/// <c>ISyncClient.LocalChangePushed</c> no <c>LocalSyncClient</c> real (SQLCipher via SyncTestContext).
/// </summary>
public sealed class LocalSyncClientPushSignalTests
{
    private static SyncChange Change(string entityId, string? clientChangeId = null) => new()
    {
        ClientChangeId = clientChangeId,
        EntityType = "asset",
        EntityId = entityId,
        Operation = "created",
        Patch = [],
    };

    [Fact]
    public async Task Fires_When_A_Change_Is_Written()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-push-signal");
        int fired = 0;
        ctx.Client.LocalChangePushed += () => fired++;

        await ctx.Client.PushAsync([Change("e1")]);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Fires_Once_Per_Push_Not_Once_Per_Change()
    {
        // Uma edição pode empurrar várias linhas (ativo + endpoint); o gatilho é por PUSH, não por
        // linha — o consumidor debounça de qualquer jeito.
        using var ctx = await SyncTestContext.CreateAsync("ws-push-batch");
        int fired = 0;
        ctx.Client.LocalChangePushed += () => fired++;

        await ctx.Client.PushAsync([Change("e1"), Change("e2"), Change("e3")]);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Does_Not_Fire_On_Empty_Push()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-push-empty");
        int fired = 0;
        ctx.Client.LocalChangePushed += () => fired++;

        await ctx.Client.PushAsync([]);

        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task Does_Not_Fire_When_Push_Is_A_Noop_Duplicate()
    {
        // INSERT OR IGNORE por client_change_id: reenviar o MESMO id não grava nada — nada novo pra
        // sincronizar, nada de sinal.
        using var ctx = await SyncTestContext.CreateAsync("ws-push-dup");
        int fired = 0;
        ctx.Client.LocalChangePushed += () => fired++;

        await ctx.Client.PushAsync([Change("e1", clientChangeId: "cid-1")]);
        await ctx.Client.PushAsync([Change("e1", clientChangeId: "cid-1")]); // duplicado exato

        Assert.Equal(1, fired);
    }
}
