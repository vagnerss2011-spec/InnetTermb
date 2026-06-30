using RemoteOps.Contracts.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncClientIdempotencyTests
{
    [Fact]
    public async Task Push_SameClientChangeId_InsertedOnce()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        var change = new SyncChange
        {
            ClientChangeId = "idempotent-01",
            EntityType = "asset",
            EntityId = "a1",
            Operation = "created",
            Patch = [],
        };

        await ctx.Client.PushAsync([change]);
        await ctx.Client.PushAsync([change]); // segunda push — deve ser ignorada

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);

        Assert.Single(pulled);
    }

    [Fact]
    public async Task Push_SameClientChangeId_In_Batch_InsertedOnce()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        var change = new SyncChange
        {
            ClientChangeId = "batch-dup",
            EntityType = "asset",
            EntityId = "a1",
            Operation = "created",
            Patch = [],
        };

        // Mesma mudança duplicada no batch — apenas um registro deve persistir.
        await ctx.Client.PushAsync([change, change]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);
        Assert.Single(pulled);
    }

    [Fact]
    public async Task Push_NullClientChangeId_Each_Insert_Is_Unique()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        var change = new SyncChange
        {
            ClientChangeId = null,
            EntityType = "asset",
            EntityId = "a1",
            Operation = "updated",
            Patch = [],
        };

        // ClientChangeId = null => sem chave de idempotência; cada push insere.
        await ctx.Client.PushAsync([change]);
        await ctx.Client.PushAsync([change]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);
        Assert.Equal(2, pulled.Count);
    }

    [Fact]
    public async Task Push_Different_ClientChangeIds_Are_All_Inserted()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        await ctx.Client.PushAsync([
            new SyncChange { ClientChangeId = "c1", EntityType = "a", EntityId = "1", Operation = "created", Patch = [] },
            new SyncChange { ClientChangeId = "c2", EntityType = "a", EntityId = "2", Operation = "updated", Patch = [] },
            new SyncChange { ClientChangeId = "c3", EntityType = "a", EntityId = "3", Operation = "deleted", Patch = [] },
        ]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);
        Assert.Equal(3, pulled.Count);
    }
}
