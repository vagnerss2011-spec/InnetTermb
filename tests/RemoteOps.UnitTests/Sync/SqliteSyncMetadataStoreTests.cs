using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Testes do metadata store sobre SQLCipher real: cursores (server + outbox), persistência de
/// conflitos e migração compatível por cima do schema legado criado pelo LocalSyncClient.
/// </summary>
public sealed class SqliteSyncMetadataStoreTests
{
    private static ConflictDetail Conflict(string reason)
        => new("c1", "SecretEnvelope", "s1", 1, -1, reason);

    [Fact]
    public async Task GetCursors_Defaults_To_Zero()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-default");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-default");

        Assert.Equal(0, cursors.ServerCursor);
        Assert.Equal(0, cursors.OutboxCursor);
    }

    [Fact]
    public async Task Save_And_Get_Server_And_Outbox_Cursors()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-cursors");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.SaveServerCursorAsync("ws-md-cursors", 42);
        await store.SaveOutboxCursorAsync("ws-md-cursors", 7);

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-cursors");
        Assert.Equal(42, cursors.ServerCursor);
        Assert.Equal(7, cursors.OutboxCursor);
    }

    [Fact]
    public async Task Save_Server_Cursor_Preserves_Outbox_Cursor()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-preserve");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);
        await store.SaveOutboxCursorAsync("ws-md-preserve", 9);

        await store.SaveServerCursorAsync("ws-md-preserve", 100);

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-preserve");
        Assert.Equal(100, cursors.ServerCursor);
        Assert.Equal(9, cursors.OutboxCursor);
    }

    [Fact]
    public async Task RecordConflicts_Increments_Count_And_Persists_Reason()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-conflicts");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.RecordConflictsAsync(
            [Conflict("secret-envelope.no-auto-merge"), Conflict("version.conflict")]);

        Assert.Equal(2, await store.GetConflictCountAsync());

        using SqliteConnection conn = await ctx.Workspace.OpenConnectionAsync();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM conflicts WHERE reason = 'secret-envelope.no-auto-merge'";
        long n = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Server_Cursor_Is_Monotonic_Never_Regresses()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-mono-srv");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.SaveServerCursorAsync("ws-md-mono-srv", 150);
        await store.SaveServerCursorAsync("ws-md-mono-srv", 120); // save tardio/obsoleto não regride

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-mono-srv");
        Assert.Equal(150, cursors.ServerCursor);
    }

    [Fact]
    public async Task Outbox_Cursor_Is_Monotonic_Never_Regresses()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-mono-out");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.SaveOutboxCursorAsync("ws-md-mono-out", 400);
        await store.SaveOutboxCursorAsync("ws-md-mono-out", 200); // não regride abaixo do já gravado

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-mono-out");
        Assert.Equal(400, cursors.OutboxCursor);
    }

    [Fact]
    public async Task Migrates_Compatibly_Over_Legacy_Schema()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-md-migrate");

        // LocalSyncClient.PushAsync cria o schema LEGADO de conflicts + sync_cursor primeiro.
        await ctx.Client.PushAsync(
        [
            new SyncChange { EntityType = "asset", EntityId = "e1", Operation = "created", Patch = [] },
        ]);

        var store = new SqliteSyncMetadataStore(ctx.Workspace);
        await store.SaveOutboxCursorAsync("ws-md-migrate", 3);          // exige coluna outbox_cursor
        await store.RecordConflictsAsync([Conflict("version.conflict")]); // exige colunas do ConflictDetail

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-migrate");
        Assert.Equal(3, cursors.OutboxCursor);
        Assert.Equal(1, await store.GetConflictCountAsync());
    }
}
