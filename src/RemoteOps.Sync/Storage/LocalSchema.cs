using Microsoft.Data.Sqlite;

namespace RemoteOps.Sync.Storage;

/// <summary>
/// O schema das tabelas de METADADOS do banco local (grupos, ativos, endpoints, refs de credencial)
/// — num lugar só, de propósito.
///
/// <para><b>Por que existe:</b> até a Fase 1 o schema morava dentro do <c>SqlCipherLocalStore</c>
/// (RemoteOps.Desktop) e o applier do changelog (RemoteOps.Sync) escrevia numa tabela genérica
/// própria. Eram DUAS verdades sobre "onde os dados do operador moram", e a consequência foi o furo
/// que esta fase fecha: o changelog puxado caía num cache que a UI não lia, e o device B mostrava
/// uma lista de hosts VAZIA. Com o schema aqui, o store e o applier escrevem literalmente nas mesmas
/// tabelas — e a divergência deixa de ser possível por construção.</para>
///
/// <para>Desktop referencia Sync (não o contrário), então este é o assembly que os dois alcançam.</para>
///
/// <para><b>Migração aditiva e idempotente</b> (padrão do repo): <c>CREATE TABLE IF NOT EXISTS</c> +
/// <c>PRAGMA table_info</c>/<c>ALTER TABLE</c>. Um banco de uma versão anterior abre e ganha as
/// colunas novas sem tocar no que já existe.</para>
/// </summary>
public static class LocalSchema
{
    /// <summary>Cria as tabelas de metadados se faltarem e aplica as migrações aditivas.</summary>
    public static async Task EnsureAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS asset_groups (
                    id                        TEXT PRIMARY KEY,
                    workspace_id              TEXT NOT NULL,
                    parent_id                 TEXT,
                    name                      TEXT NOT NULL,
                    default_credential_ref_id TEXT,
                    version                   INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_ag_workspace ON asset_groups (workspace_id);

                CREATE TABLE IF NOT EXISTS assets (
                    id           TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    group_id     TEXT,
                    name         TEXT NOT NULL,
                    vendor       TEXT,
                    model        TEXT,
                    device_role  TEXT,
                    site         TEXT,
                    tags_json    TEXT NOT NULL DEFAULT '[]',
                    version      INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_assets_workspace ON assets (workspace_id);

                CREATE TABLE IF NOT EXISTS endpoints (
                    id                TEXT PRIMARY KEY,
                    asset_id          TEXT NOT NULL,
                    protocol          TEXT NOT NULL,
                    fqdn              TEXT,
                    ipv4              TEXT,
                    ipv6              TEXT,
                    port              INTEGER NOT NULL DEFAULT 0,
                    prefer_ipv6       INTEGER NOT NULL DEFAULT 1,
                    credential_ref_id TEXT,
                    profile_json      TEXT,
                    version           INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_ep_asset ON endpoints (asset_id);

                CREATE TABLE IF NOT EXISTS credential_refs (
                    id                 TEXT PRIMARY KEY,
                    name               TEXT NOT NULL,
                    type               TEXT NOT NULL,
                    scope              TEXT,
                    metadata_json      TEXT,
                    secret_envelope_id TEXT,
                    version            INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_cred_scope ON credential_refs (scope);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Migração idempotente: bancos criados antes do classificador (v1.2.24-) não têm a coluna
        // device_role — CREATE TABLE IF NOT EXISTS não altera tabela existente. Adiciona se faltar.
        await EnsureColumnAsync(conn, "assets", "device_role", "TEXT", ct);
    }

    /// <summary>
    /// Adiciona <paramref name="column"/> a <paramref name="table"/> se ainda não existir (via
    /// PRAGMA table_info). Nomes são constantes internas (não entrada do usuário) — sem risco de
    /// injeção. ALTER TABLE ADD COLUMN é barato e idempotente por esta checagem.
    /// </summary>
    private static async Task EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        bool exists = false;
        using (SqliteCommand check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using SqliteDataReader r = await check.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using SqliteCommand alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            await alter.ExecuteNonQueryAsync(ct);
        }
    }
}
