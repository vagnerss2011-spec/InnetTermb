using System.Text.Json;

using RemoteOps.Contracts.Sync;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncClientRoundTripTests
{
    [Fact]
    public async Task Push_Then_Pull_Returns_All_Changes()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        var changes = new[]
        {
            Change("asset", "a1", "created"),
            Change("asset", "a2", "updated"),
            Change("endpoint", "e1", "deleted"),
        };

        await ctx.Client.PushAsync(changes);
        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);

        Assert.Equal(3, pulled.Count);
    }

    [Fact]
    public async Task Pull_Returns_Changes_Ordered_By_Cursor_Ascending()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        await ctx.Client.PushAsync([Change("asset", "a1", "created")]);
        await ctx.Client.PushAsync([Change("asset", "a2", "updated")]);
        await ctx.Client.PushAsync([Change("asset", "a3", "deleted")]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);

        Assert.Equal("a1", pulled[0].EntityId);
        Assert.Equal("a2", pulled[1].EntityId);
        Assert.Equal("a3", pulled[2].EntityId);
    }

    [Fact]
    public async Task Pull_Preserves_Patch_Json_Roundtrip()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        var patch = new Dictionary<string, object?> { ["name"] = "Router-01", ["port"] = 22 };
        await ctx.Client.PushAsync([
            new SyncChange
            {
                EntityType = "asset",
                EntityId = "a1",
                Operation = "created",
                Patch = patch,
            }
        ]);

        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);

        Assert.Single(pulled);
        // Compara via JSON: JsonElement != boxed types mas serializa igual.
        Assert.Equal(
            JsonSerializer.Serialize(patch),
            JsonSerializer.Serialize(pulled[0].Patch));
    }

    [Fact]
    public async Task Pull_Preserves_All_SyncChange_Fields()
    {
        using var ctx = await SyncTestContext.CreateAsync();

        var original = new SyncChange
        {
            ClientChangeId = "cid-preserve-test",
            EntityType = "endpoint",
            EntityId = "ep-1",
            Operation = "updated",
            BaseVersion = 7,
            Patch = new Dictionary<string, object?> { ["port"] = 8080 },
        };

        await ctx.Client.PushAsync([original]);
        IReadOnlyList<SyncChange> pulled = await ctx.Client.PullAsync(0);

        Assert.Single(pulled);
        SyncChange result = pulled[0];
        Assert.Equal("cid-preserve-test", result.ClientChangeId);
        Assert.Equal("endpoint", result.EntityType);
        Assert.Equal("ep-1", result.EntityId);
        Assert.Equal("updated", result.Operation);
        Assert.Equal(7, result.BaseVersion);
    }

    private static SyncChange Change(string entityType, string entityId, string operation) =>
        new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Operation = operation,
            Patch = [],
        };
}
