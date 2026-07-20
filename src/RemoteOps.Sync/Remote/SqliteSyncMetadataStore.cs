using System.Globalization;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// <see cref="ISyncMetadataStore"/> sobre o banco SQLCipher do workspace (via
/// <see cref="WorkspaceContext"/>): cursores em <c>sync_cursor</c> (com as colunas
/// <c>outbox_cursor</c> e <c>secrets_cursor</c>), conflitos em <c>conflicts</c> (com as colunas do
/// <see cref="ConflictDetail"/>) e o ledger do canal de segredos em <c>secrets_pushed</c>.
/// Migração aditiva e idempotente (ADR-013): tudo é <c>CREATE TABLE IF NOT EXISTS</c> +
/// <c>PRAGMA table_info</c>/<c>ALTER TABLE</c>, então um banco de uma versão anterior do app abre e
/// ganha as colunas novas sem tocar no que já existe.
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

    public async Task<IReadOnlyList<StoredConflict>> GetConflictsAsync(
        int limit, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        // rowid como desempate: detected_at é string ISO, e uma rajada de conflitos do MESMO ciclo
        // carimba praticamente o mesmo instante — sem o desempate a ordem sairia ao acaso.
        cmd.CommandText = """
            SELECT entity_type, entity_id, detected_at, base_version, current_version, reason
            FROM conflicts
            ORDER BY detected_at DESC, rowid DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var result = new List<StoredConflict>();
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            DateTimeOffset detectedAt = !reader.IsDBNull(2)
                && DateTimeOffset.TryParse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            result.Add(new StoredConflict(
                EntityType: reader.GetString(0),
                EntityId: reader.GetString(1),
                DetectedAt: detectedAt,
                BaseVersion: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                CurrentVersion: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Reason: reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
        }

        return result;
    }

    public async Task ClearConflictsAsync(CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conflicts";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Canal de segredos ────────────────────────────────────────────────────────────────

    public async Task<long> GetSecretsCursorAsync(string workspaceId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT secrets_cursor FROM sync_cursor WHERE workspace_id = $ws";
        cmd.Parameters.AddWithValue("$ws", workspaceId);

        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public async Task SaveSecretsCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        // MAX(...): mesma disciplina monotônica dos outros cursores — um save tardio nunca regride.
        cmd.CommandText = """
            INSERT INTO sync_cursor (workspace_id, cursor, outbox_cursor, secrets_cursor)
            VALUES ($ws, 0, 0, $secrets)
            ON CONFLICT (workspace_id) DO UPDATE SET
                secrets_cursor = MAX(secrets_cursor, excluded.secrets_cursor);
            """;
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$secrets", cursor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetPushedSecretsAsync(
        string workspaceId, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT envelope_id, version FROM secrets_pushed WHERE workspace_id = $ws";
        cmd.Parameters.AddWithValue("$ws", workspaceId);

        var pushed = new Dictionary<string, int>(StringComparer.Ordinal);
        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pushed[reader.GetString(0)] = reader.GetInt32(1);
        }

        return pushed;
    }

    public async Task MarkSecretPushedAsync(
        string workspaceId, string envelopeId, int version, CancellationToken ct = default)
    {
        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO secrets_pushed (workspace_id, envelope_id, version) VALUES ($ws, $eid, $ver)
            ON CONFLICT (workspace_id, envelope_id) DO UPDATE SET version = MAX(version, excluded.version);
            """;
        cmd.Parameters.AddWithValue("$ws", workspaceId);
        cmd.Parameters.AddWithValue("$eid", envelopeId);
        cmd.Parameters.AddWithValue("$ver", version);
        await cmd.ExecuteNonQueryAsync(ct);
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

                -- Ledger do canal de segredos: que envelope já está no servidor, e em que versão.
                -- Só id + versão: nenhum material de envelope encosta aqui.
                CREATE TABLE IF NOT EXISTS secrets_pushed (
                    workspace_id TEXT    NOT NULL,
                    envelope_id  TEXT    NOT NULL,
                    version      INTEGER NOT NULL,
                    PRIMARY KEY (workspace_id, envelope_id)
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await EnsureColumnAsync(conn, "sync_cursor", "outbox_cursor", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(conn, "sync_cursor", "secrets_cursor", "INTEGER NOT NULL DEFAULT 0", ct);
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
