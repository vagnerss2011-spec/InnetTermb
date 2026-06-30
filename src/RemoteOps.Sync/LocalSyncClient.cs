using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Storage;

namespace RemoteOps.Sync;

/// <summary>
/// Implementação de <see cref="ISyncClient"/> sobre SQLite/SQLCipher local (ADR-008).
/// A chave do banco é derivada e protegida pelo vault (ADR-003).
///
/// <list type="bullet">
///   <item><see cref="PushAsync"/> — grava lote no outbox local, idempotente por ClientChangeId.</item>
///   <item><see cref="PullAsync"/> — lê o outbox a partir do cursor, paginado, crescente.</item>
///   <item>Conflitos são registrados em <c>conflicts</c> (ADR-002); nenhum merge automático de segredos.</item>
///   <item>Nenhum segredo ou patch sensível em logs ou exceções.</item>
/// </list>
/// </summary>
public sealed class LocalSyncClient : ISyncClient
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    private readonly IDbConnectionFactory _connectionFactory;
    private long _currentCursor;

    public long CurrentCursor => Volatile.Read(ref _currentCursor);

    internal LocalSyncClient(IDbConnectionFactory connectionFactory, long initialCursor = 0)
    {
        _connectionFactory = connectionFactory;
        _currentCursor = initialCursor;
    }

    public async Task PushAsync(IEnumerable<SyncChange> changes, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _connectionFactory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        foreach (SyncChange change in changes)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO local_outbox
                    (client_change_id, entity_type, entity_id, operation, base_version, patch_json, created_at)
                VALUES
                    ($cid, $et, $eid, $op, $bv, $patch, $ts)
                """;
            cmd.Parameters.AddWithValue("$cid", (object?)change.ClientChangeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$et", change.EntityType);
            cmd.Parameters.AddWithValue("$eid", change.EntityId);
            cmd.Parameters.AddWithValue("$op", change.Operation);
            cmd.Parameters.AddWithValue("$bv", change.BaseVersion);
            cmd.Parameters.AddWithValue("$patch", JsonSerializer.Serialize(change.Patch, s_json));
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<SyncChange>> PullAsync(
        long fromCursor, int limit = 500, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _connectionFactory.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, client_change_id, entity_type, entity_id, operation, base_version, patch_json
            FROM local_outbox
            WHERE id > $from
            ORDER BY id ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$from", fromCursor);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<SyncChange>();
        long maxCursor = fromCursor;

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long rowId = reader.GetInt64(0);
            string? clientChangeId = reader.IsDBNull(1) ? null : reader.GetString(1);
            string entityType = reader.GetString(2);
            string entityId = reader.GetString(3);
            string operation = reader.GetString(4);
            int baseVersion = reader.GetInt32(5);
            string patchJson = reader.GetString(6);

            Dictionary<string, object?> patch =
                JsonSerializer.Deserialize<Dictionary<string, object?>>(patchJson, s_json)
                ?? [];

            results.Add(new SyncChange
            {
                ClientChangeId = clientChangeId,
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                BaseVersion = baseVersion,
                Patch = patch,
            });

            if (rowId > maxCursor)
            {
                maxCursor = rowId;
            }
        }

        if (results.Count > 0)
        {
            Volatile.Write(ref _currentCursor, maxCursor);
        }

        return results;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_outbox (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                client_change_id TEXT,
                entity_type      TEXT    NOT NULL,
                entity_id        TEXT    NOT NULL,
                operation        TEXT    NOT NULL
                                         CHECK (operation IN ('created', 'updated', 'deleted')),
                base_version     INTEGER NOT NULL DEFAULT 0,
                patch_json       TEXT    NOT NULL,
                created_at       TEXT    NOT NULL,
                UNIQUE (client_change_id)
            );

            CREATE INDEX IF NOT EXISTS idx_outbox_entity
                ON local_outbox (entity_id, entity_type);

            CREATE TABLE IF NOT EXISTS local_entities (
                entity_type TEXT    NOT NULL,
                entity_id   TEXT    NOT NULL,
                version     INTEGER NOT NULL DEFAULT 0,
                data_json   TEXT    NOT NULL,
                updated_at  TEXT    NOT NULL,
                PRIMARY KEY (entity_type, entity_id)
            );

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
}
