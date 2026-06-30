using System.Text.Json;

using Microsoft.Data.Sqlite;

using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação canônica de <see cref="IRemoteChangeApplier"/>: aplica mudanças puxadas do servidor
/// na tabela <c>local_entities</c> do mesmo banco SQLCipher (via <see cref="WorkspaceContext"/>), de
/// forma idempotente e monotônica, e SEM gravar no outbox (não usa <c>ISyncClient.PushAsync</c>),
/// evitando loop de eco. Ver ADR-013.
/// </summary>
public sealed class LocalEntitiesChangeApplier : IRemoteChangeApplier
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly WorkspaceContext _workspace;

    public LocalEntitiesChangeApplier(WorkspaceContext workspace)
    {
        _workspace = workspace;
    }

    public async Task ApplyAsync(IReadOnlyList<SyncChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0)
        {
            return;
        }

        using SqliteConnection conn = await _workspace.OpenConnectionAsync(ct);
        await EnsureSchemaAsync(conn, ct);

        foreach (SyncChange change in changes)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(change.Operation, "deleted", StringComparison.Ordinal))
            {
                await DeleteAsync(conn, change, ct);
            }
            else
            {
                await UpsertAsync(conn, change, ct);
            }
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS local_entities (
                entity_type TEXT    NOT NULL,
                entity_id   TEXT    NOT NULL,
                version     INTEGER NOT NULL DEFAULT 0,
                data_json   TEXT    NOT NULL,
                updated_at  TEXT    NOT NULL,
                PRIMARY KEY (entity_type, entity_id)
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // UPSERT monotônico: só sobrescreve quando a versão recebida é >= a local. Re-aplicar a mesma
    // versão é no-op (idempotente); versão mais antiga é ignorada (sem downgrade).
    private static async Task UpsertAsync(SqliteConnection conn, SyncChange change, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_entities (entity_type, entity_id, version, data_json, updated_at)
            VALUES ($et, $eid, $ver, $data, $ts)
            ON CONFLICT (entity_type, entity_id) DO UPDATE SET
                version    = excluded.version,
                data_json  = excluded.data_json,
                updated_at = excluded.updated_at
            WHERE excluded.version >= local_entities.version;
            """;
        cmd.Parameters.AddWithValue("$et", change.EntityType);
        cmd.Parameters.AddWithValue("$eid", change.EntityId);
        cmd.Parameters.AddWithValue("$ver", change.BaseVersion + 1);
        cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(change.Patch, s_json));
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteAsync(SqliteConnection conn, SyncChange change, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_entities WHERE entity_type = $et AND entity_id = $eid";
        cmd.Parameters.AddWithValue("$et", change.EntityType);
        cmd.Parameters.AddWithValue("$eid", change.EntityId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
