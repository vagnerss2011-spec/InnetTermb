using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Testes do applier sobre SQLCipher real: created/updated/deleted, idempotência, monotonicidade
/// e ausência de re-emissão no outbox (sem loop de eco).
/// </summary>
public sealed class LocalEntitiesChangeApplierTests
{
    private static SyncChange Change(string id, string op, int baseVersion, object? name = null)
        => new()
        {
            EntityType = "asset",
            EntityId = id,
            Operation = op,
            BaseVersion = baseVersion,
            Patch = name is null ? [] : new Dictionary<string, object?> { ["name"] = name },
        };

    private static async Task<(bool Exists, long Version, string Data)> ReadEntityAsync(
        SyncTestContext ctx, string entityType, string entityId)
    {
        using SqliteConnection conn = await ctx.Workspace.OpenConnectionAsync();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT version, data_json FROM local_entities WHERE entity_type=$et AND entity_id=$eid";
        cmd.Parameters.AddWithValue("$et", entityType);
        cmd.Parameters.AddWithValue("$eid", entityId);

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (true, reader.GetInt64(0), reader.GetString(1))
            : (false, 0, string.Empty);
    }

    [Fact]
    public async Task Apply_Created_Inserts_Row_With_Version()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-create");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("e1", "created", 0, "r1")]);

        (bool exists, long version, string data) = await ReadEntityAsync(ctx, "asset", "e1");
        Assert.True(exists);
        Assert.Equal(1, version);
        Assert.Contains("r1", data);
    }

    [Fact]
    public async Task Apply_Updated_Overwrites_Row()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-update");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("e1", "created", 0, "r1")]);

        await applier.ApplyAsync([Change("e1", "updated", 1, "r2")]);

        (bool exists, long version, string data) = await ReadEntityAsync(ctx, "asset", "e1");
        Assert.True(exists);
        Assert.Equal(2, version);
        Assert.Contains("r2", data);
        Assert.DoesNotContain("r1", data);
    }

    [Fact]
    public async Task Apply_Deleted_Removes_Row()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-delete");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("e1", "created", 0, "r1")]);

        await applier.ApplyAsync([Change("e1", "deleted", 1)]);

        (bool exists, _, _) = await ReadEntityAsync(ctx, "asset", "e1");
        Assert.False(exists);
    }

    [Fact]
    public async Task Apply_Is_Idempotent_For_Same_Change()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-idem");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("e1", "created", 0, "r1")]);
        await applier.ApplyAsync([Change("e1", "created", 0, "r1")]);

        (bool exists, long version, _) = await ReadEntityAsync(ctx, "asset", "e1");
        Assert.True(exists);
        Assert.Equal(1, version);
    }

    [Fact]
    public async Task Apply_Does_Not_Downgrade_To_Older_Version()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-monotonic");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);
        await applier.ApplyAsync([Change("e1", "updated", 4, "new")]);

        await applier.ApplyAsync([Change("e1", "updated", 0, "old")]);

        (bool exists, long version, string data) = await ReadEntityAsync(ctx, "asset", "e1");
        Assert.True(exists);
        Assert.Equal(5, version);
        Assert.Contains("new", data);
    }

    [Fact]
    public async Task Apply_Does_Not_Emit_To_Outbox()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-noecho");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        await applier.ApplyAsync([Change("e1", "created", 0, "r1")]);

        IReadOnlyList<SyncChange> outbox = await ctx.Client.PullAsync(0);
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task Apply_Skips_SecretEnvelope_Changes()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-secret");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        // SecretEnvelope NUNCA é aplicado/mesclado no cliente (CLAUDE.md / ADR-003). Mesmo misturado
        // com mudanças normais, é ignorado; o asset legítimo é aplicado.
        var secret = new SyncChange
        {
            EntityType = "SecretEnvelope",
            EntityId = "se1",
            Operation = "updated",
            BaseVersion = 3,
            Patch = new Dictionary<string, object?> { ["keyVersion"] = 2 },
        };

        await applier.ApplyAsync([secret, Change("a1", "created", 0, "r1")]);

        (bool secretExists, _, _) = await ReadEntityAsync(ctx, "SecretEnvelope", "se1");
        (bool assetExists, _, _) = await ReadEntityAsync(ctx, "asset", "a1");
        Assert.False(secretExists);
        Assert.True(assetExists);
    }

    [Fact]
    public async Task Apply_Skips_SecretEnvelope_Delete()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-app-secret-del");
        var applier = new LocalEntitiesChangeApplier(ctx.Workspace);

        var del = new SyncChange
        {
            EntityType = "SecretEnvelope",
            EntityId = "se1",
            Operation = "deleted",
            BaseVersion = 0,
            Patch = [],
        };

        // Não lança e não toca local_entities (segregado antes do DELETE).
        await applier.ApplyAsync([del]);

        (bool exists, _, _) = await ReadEntityAsync(ctx, "SecretEnvelope", "se1");
        Assert.False(exists);
    }
}
