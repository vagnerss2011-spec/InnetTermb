using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// <see cref="ISyncMetadataStore"/> sobre o banco SQLCipher do workspace (via
/// <see cref="WorkspaceContext"/>): cursores em <c>sync_cursor</c> (com a coluna nova
/// <c>outbox_cursor</c>) e conflitos em <c>conflicts</c> (com as colunas do
/// <see cref="ConflictDetail"/>). Migração aditiva e idempotente (ADR-013).
/// </summary>
public sealed class SqliteSyncMetadataStore : ISyncMetadataStore
{
    private readonly WorkspaceContext _workspace;

    public SqliteSyncMetadataStore(WorkspaceContext workspace)
    {
        _workspace = workspace;
    }

    public async Task<SyncCursors> GetCursorsAsync(string workspaceId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT cursor, outbox_cursor FROM sync_cursor WHERE workspace_id = $ws";
        cmd.Parameters.AddWithValue("$ws", workspaceId);

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new SyncCursors(reader.GetInt64(0), reader.GetInt64(1))
            : new SyncCursors(0, 0);
    }

    public async Task SaveServerCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        // MAX(...) garante avanço monotônico: um save tardio nunca regride o cursor abaixo de um valor
        // já gravado (defesa em profundidade — a serialização do SyncOrchestrator já evita a corrida).
        cmd.CommandText = """
            INSERT INTO sync_cursor (workspace_id, cursor, outbox_cursor) VALUES ($ws, $cursor, 0)
            ON CONFLICT (workspace_id) DO UPDATE SET cursor = MAX(cursor, excluded.cursor);
            """;
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$cursor", cursor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveOutboxCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_cursor (workspace_id, cursor, outbox_cursor) VALUES ($ws, 0, $outbox)
            ON CONFLICT (workspace_id) DO UPDATE SET outbox_cursor = MAX(outbox_cursor, excluded.outbox_cursor);
            """;
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$outbox", cursor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordConflictsAsync(
        IReadOnlyList<ConflictDetail> conflicts, CancellationToken ct = default)
    {
        if (conflicts.Count == 0)
        {
            return;
        }

        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        foreach (ConflictDetail conflict in conflicts)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conflicts
                    (entity_type, entity_id, local_patch_json, server_patch_json, detected_at,
                     client_change_id, base_version, current_version, reason)
                VALUES
                    ($et, $eid, '{}', '{}', $ts, $cid, $bv, $cv, $reason);
                """;
            cmd.Parameters.AddWithValue("$et", conflict.EntityType);
            cmd.Parameters.AddWithValue("$eid", conflict.EntityId);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$cid", (object?)conflict.ClientChangeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bv", conflict.BaseVersion);
            cmd.Parameters.AddWithValue("$cv", conflict.CurrentVersion);
            cmd.Parameters.AddWithValue("$reason", conflict.Reason);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<int> GetConflictCountAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM conflicts";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    // Migração aditiva e idempotente: cria as tabelas se faltarem e adiciona, via ALTER, a coluna
    // outbox_cursor (sync_cursor) e as colunas do ConflictDetail (conflicts) quando ausentes — ADR-013.
    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sync_cursor (
                    workspace_id TEXT    NOT NULL PRIMARY KEY,
                    cursor       INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS conflicts (
                    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_type        TEXT    NOT NULL,
                    entity_id          TEXT    NOT NULL,
                    local_patch_json   TEXT    NOT NULL,
                    server_patch_json  TEXT    NOT NULL,
                    detected_at        TEXT    NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await EnsureColumnAsync(conn, "sync_cursor", "outbox_cursor", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(conn, "conflicts", "client_change_id", "TEXT", ct);
        await EnsureColumnAsync(conn, "conflicts", "base_version", "INTEGER", ct);
        await EnsureColumnAsync(conn, "conflicts", "current_version", "INTEGER", ct);
        await EnsureColumnAsync(conn, "conflicts", "reason", "TEXT", ct);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string definition, CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct))
        {
            return;
        }

        // table/column/definition são constantes do código (sem entrada de usuário) — sem injeção.
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // PRAGMA table_info: cid(0), name(1), type(2), notnull(3), dflt_value(4), pk(5)
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
