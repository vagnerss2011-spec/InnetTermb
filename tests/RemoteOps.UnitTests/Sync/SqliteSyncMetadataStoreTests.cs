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
        await store.SaveSecretsCursorAsync("ws-md-migrate", 5);         // exige coluna secrets_cursor

        SyncCursors cursors = await store.GetCursorsAsync("ws-md-migrate");
        Assert.Equal(3, cursors.OutboxCursor);
        Assert.Equal(1, await store.GetConflictCountAsync());
        Assert.Equal(5, await store.GetSecretsCursorAsync("ws-md-migrate"));
    }

    // ── Canal de segredos ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Secrets_Cursor_Defaults_To_Zero()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-sec-default");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        Assert.Equal(0, await store.GetSecretsCursorAsync("ws-sec-default"));
    }

    [Fact]
    public async Task Secrets_Cursor_Is_Monotonic_Never_Regresses()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-sec-mono");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.SaveSecretsCursorAsync("ws-sec-mono", 90);
        await store.SaveSecretsCursorAsync("ws-sec-mono", 30); // save tardio não regride

        Assert.Equal(90, await store.GetSecretsCursorAsync("ws-sec-mono"));
    }

    /// <summary>
    /// Os três cursores são independentes: avançar o de segredos não pode mexer nos de metadados
    /// (e vice-versa) — eles vêm de canais diferentes, com posições diferentes no servidor.
    /// </summary>
    [Fact]
    public async Task Secrets_Cursor_Does_Not_Disturb_Other_Cursors()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-sec-indep");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.SaveServerCursorAsync("ws-sec-indep", 11);
        await store.SaveOutboxCursorAsync("ws-sec-indep", 22);
        await store.SaveSecretsCursorAsync("ws-sec-indep", 33);

        SyncCursors cursors = await store.GetCursorsAsync("ws-sec-indep");
        Assert.Equal(11, cursors.ServerCursor);
        Assert.Equal(22, cursors.OutboxCursor);
        Assert.Equal(33, await store.GetSecretsCursorAsync("ws-sec-indep"));
    }

    [Fact]
    public async Task PushedSecrets_Ledger_RoundTrips_And_Is_Monotonic()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-sec-ledger");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.MarkSecretPushedAsync("ws-sec-ledger", "env-1", 1);
        await store.MarkSecretPushedAsync("ws-sec-ledger", "env-2", 5);
        await store.MarkSecretPushedAsync("ws-sec-ledger", "env-1", 3); // rotação: sobe
        await store.MarkSecretPushedAsync("ws-sec-ledger", "env-2", 2); // tardio: não regride

        IReadOnlyDictionary<string, int> pushed = await store.GetPushedSecretsAsync("ws-sec-ledger");

        Assert.Equal(2, pushed.Count);
        Assert.Equal(3, pushed["env-1"]);
        Assert.Equal(5, pushed["env-2"]);
    }

    /// <summary>O ledger é POR workspace: um não pode enxergar (nem suprimir) o push do outro.</summary>
    [Fact]
    public async Task PushedSecrets_Ledger_Is_Scoped_By_Workspace()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-sec-scope");
        var store = new SqliteSyncMetadataStore(ctx.Workspace);

        await store.MarkSecretPushedAsync("ws-a", "env-1", 1);
        await store.MarkSecretPushedAsync("ws-b", "env-2", 1);

        Assert.Equal("env-1", Assert.Single(await store.GetPushedSecretsAsync("ws-a")).Key);
        Assert.Equal("env-2", Assert.Single(await store.GetPushedSecretsAsync("ws-b")).Key);
    }

    /// <summary>O ledger sobrevive ao restart: senão todo boot re-subiria o cofre inteiro.</summary>
    [Fact]
    public async Task PushedSecrets_Ledger_Survives_Reopen()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-sec-restart");

        await new SqliteSyncMetadataStore(ctx.Workspace).MarkSecretPushedAsync("ws-sec-restart", "env-1", 2);
        await new SqliteSyncMetadataStore(ctx.Workspace).SaveSecretsCursorAsync("ws-sec-restart", 8);

        var reopened = new SqliteSyncMetadataStore(ctx.Workspace);
        Assert.Equal(2, (await reopened.GetPushedSecretsAsync("ws-sec-restart"))["env-1"]);
        Assert.Equal(8, await reopened.GetSecretsCursorAsync("ws-sec-restart"));
    }
}
