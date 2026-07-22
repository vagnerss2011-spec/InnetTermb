using Microsoft.Data.Sqlite;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// ⚠️ <b>Uma definição só de "o que ainda não subiu deste banco".</b>
///
/// <para>Dois lugares fazem essa mesma pergunta e por motivos diferentes: o
/// <see cref="OtherVaultOutboxProbe"/> (no boot, sobre os bancos dos OUTROS cofres) e o
/// <see cref="VaultSwitch"/> (na hora de trocar de cofre, sobre o banco DESTA sessão — justamente o
/// que a sonda pula). Duas cópias do SQL divergiriam no primeiro ajuste, e a divergência apareceria
/// como "o aviso diz um número e a troca diz outro" — que é pior do que não ter aviso nenhum.</para>
///
/// <para><b>As consultas são cruas de propósito.</b> Passar pelo <c>SqliteSyncMetadataStore</c>
/// traria o <c>EnsureSchema</c> junto (<c>CREATE TABLE</c>/<c>ALTER TABLE</c>): migrar um banco por
/// causa de um aviso trocaria de lugar o risco que o aviso vem reduzir.</para>
/// </summary>
internal static class OutboxBacklog
{
    /// <summary>
    /// Quantas edições estão DEPOIS do cursor do outbox naquela conexão. Zero aqui é sempre uma
    /// resposta MEDIDA — quem não conseguiu medir trata a exceção e diz "não verificado", nunca zero.
    /// </summary>
    internal static async Task<int> CountPendingAsync(SqliteConnection conn, CancellationToken ct)
    {
        // Banco recém-criado, que nunca recebeu edição: sem outbox não há fila.
        if (!await TableExistsAsync(conn, "local_outbox", ct).ConfigureAwait(false))
        {
            return 0;
        }

        long cursor = await ReadOutboxCursorAsync(conn, ct).ConfigureAwait(false);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_outbox WHERE id > $cursor;";
        cmd.Parameters.AddWithValue("$cursor", cursor);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// O cursor do outbox daquele banco. <c>MAX</c> entre as linhas de propósito: um banco com mais
    /// de um workspace registrado (não acontece hoje — um banco por escopo) daria o cursor mais
    /// adiantado, ou seja, MENOS pendências. Errar para menos custa um aviso que não apareceu; errar
    /// para mais custa um alarme falso recorrente, e alarme falso mata o aviso verdadeiro.
    /// </summary>
    private static async Task<long> ReadOutboxCursorAsync(SqliteConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "sync_cursor", ct).ConfigureAwait(false)
            || !await ColumnExistsAsync(conn, "sync_cursor", "outbox_cursor", ct).ConfigureAwait(false))
        {
            // Banco de uma versão anterior à coluna: nada foi marcado como enviado por ela, então
            // zero é a leitura correta — e não um chute.
            return 0;
        }

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(outbox_cursor), 0) FROM sync_cursor;";
        object? valor = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return valor is null or DBNull ? 0 : Convert.ToInt64(valor);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();

        // `table` é constante do código (sem entrada de usuário) — sem injeção.
        cmd.CommandText = $"PRAGMA table_info({table});";

        using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
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
