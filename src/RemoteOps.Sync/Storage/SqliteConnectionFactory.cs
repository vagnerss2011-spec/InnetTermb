using Microsoft.Data.Sqlite;

namespace RemoteOps.Sync.Storage;

/// <summary>
/// Abre conexões SQLite/SQLCipher com a chave derivada pelo vault.
/// A chave é passada via PRAGMA key = "x'hexbytes'" — primeiro comando após Open,
/// que usa bytes raw (sem PBKDF2) conforme ADR-003 e ADR-008.
/// O campo <see cref="_hexKey"/> nunca é logado.
/// </summary>
internal sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _dbPath;
    private readonly string _hexKey;

    internal SqliteConnectionFactory(string dbPath, string hexKey)
    {
        _dbPath = dbPath;
        _hexKey = hexKey;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);

        // PRAGMA key deve ser o PRIMEIRO comando para que o SQLCipher decore o banco.
        // O formato x'...' passa bytes raw; evita a derivação PBKDF2 do modo passphrase.
        using var keyCmd = conn.CreateCommand();
        keyCmd.CommandText = $"PRAGMA key = \"x'{_hexKey}'\"";
        await keyCmd.ExecuteNonQueryAsync(ct);

        return conn;
    }
}
