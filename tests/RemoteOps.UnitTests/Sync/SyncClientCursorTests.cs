using RemoteOps.Contracts.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncClientCursorTests
{
    [Fact]
    public async Task CurrentCursor_IsZero_Initially()
    {
        using var ctx = await SyncTestContext.CreateAsync();
        Assert.Equal(0L, ctx.Client.CurrentCursor);
    }

    [Fact]
    public async Task Pull_UpdatesCurrentCursor_ToMaxIdReturned()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        await ctx.Client.PushAsync([Change("a1"), Change("a2"), Change("a3")]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);

        Assert.Equal(3, pulled.Count);
        Assert.Equal(3L, ctx.Client.CurrentCursor); // IDs 1, 2, 3 → cursor = 3
    }

    [Fact]
    public async Task Pull_FromCursor_Returns_Only_Newer_Changes()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        await ctx.Client.PushAsync([Change("a1"), Change("a2"), Change("a3")]);

        // Pull primeiro lote até cursor 1
        IReadOnlyList<SyncChange> first = await ctx.Client.PullAsync(0, limit: 1);
        Assert.Single(first);
        Assert.Equal("a1", first[0].EntityId);

        long afterFirst = ctx.Client.CurrentCursor;

        // Pull a partir do cursor anterior deve retornar o restante
        IReadOnlyList<SyncChange> rest = await ctx.Client.PullAsync(afterFirst);
        Assert.Equal(2, rest.Count);
        Assert.Equal("a2", rest[0].EntityId);
        Assert.Equal("a3", rest[1].EntityId);
    }

    [Fact]
    public async Task Pull_With_Limit_Paginates()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        for (int i = 1; i <= 10; i++)
        {
            await ctx.Client.PushAsync([Change($"a{i}")]);
        }

        IReadOnlyList<SyncChange> page1 = await ctx.Client.PullAsync(0, limit: 4);
        IReadOnlyList<SyncChange> page2 = await ctx.Client.PullAsync(ctx.Client.CurrentCursor, limit: 4);
        IReadOnlyList<SyncChange> page3 = await ctx.Client.PullAsync(ctx.Client.CurrentCursor, limit: 4);

        Assert.Equal(4, page1.Count);
        Assert.Equal(4, page2.Count);
        Assert.Equal(2, page3.Count); // restam apenas 2
    }

    [Fact]
    public async Task Pull_EmptyResult_Does_Not_Change_CurrentCursor()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        await ctx.Client.PushAsync([Change("a1")]);
        await ctx.Client.PullAsync(0); // cursor → 1

        long beforeEmpty = ctx.Client.CurrentCursor;

        // Pull além do último registro retorna lista vazia
        await ctx.Client.PullAsync(fromCursor: 999);

        Assert.Equal(beforeEmpty, ctx.Client.CurrentCursor);
    }

    [Fact]
    public async Task Pull_Cursor_IsMonotonic_Across_Pushes()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        await ctx.Client.PushAsync([Change("a1")]);
        IReadOnlyList<SyncChange> p1 = await ctx.Client.PullAsync(0);
        long c1 = ctx.Client.CurrentCursor;

        await ctx.Client.PushAsync([Change("a2")]);
        IReadOnlyList<SyncChange> p2 = await ctx.Client.PullAsync(c1);
        long c2 = ctx.Client.CurrentCursor;

        Assert.True(c2 > c1);
        Assert.Single(p1);
        Assert.Single(p2);
    }

    private static SyncChange Change(string entityId) =>
        new()
        {
            EntityType = "asset",
            EntityId = entityId,
            Operation = "created",
            Patch = [],
        };
}
