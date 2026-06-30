using Microsoft.Data.Sqlite;

namespace RemoteOps.Sync.Storage;

/// <summary>
/// Abre uma conexão com o banco local de sync. A implementação de produção usa
/// SQLCipher; implementações de teste podem substituir por SQLite em memória.
/// A conexão retornada já tem a chave aplicada e está pronta para DDL/DML.
/// </summary>
internal interface IDbConnectionFactory
{
    Task<SqliteConnection> OpenAsync(CancellationToken ct = default);
}
