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
    // Registra o provider SQLCipher (bundle_e_sqlcipher) uma vez. Necessário com
    // Microsoft.Data.Sqlite.Core, que não inicializa o provider automaticamente.
    static SqliteConnectionFactory() => SQLitePCL.Batteries_V2.Init();

    private readonly string _dbPath;
    private readonly string _hexKey;

    internal SqliteConnectionFactory(string dbPath, string hexKey)
    {
        _dbPath = dbPath;
        _hexKey = hexKey;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        // Pooling=False: conexões em pool retêm a chave do SQLCipher, o que permitiria
        // reusar uma conexão já decifrada sem reapresentar a chave (quebra de isolamento
        // por workspace). Desabilitar o pool garante que cada conexão reaplique o PRAGMA key.
        var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        try
        {
            await conn.OpenAsync(ct);

            // PRAGMA key deve ser o PRIMEIRO comando para que o SQLCipher decore o banco.
            // O formato x'...' passa bytes raw; evita a derivação PBKDF2 do modo passphrase.
            using var keyCmd = conn.CreateCommand();
            keyCmd.CommandText = $"PRAGMA key = \"x'{_hexKey}'\"";
            await keyCmd.ExecuteNonQueryAsync(ct);

            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
