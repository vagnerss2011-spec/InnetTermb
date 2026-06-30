using Microsoft.Data.Sqlite;

using RemoteOps.Sync.Storage;

namespace RemoteOps.Sync;

/// <summary>
/// Agrupa o cliente de sync e a factory de conexão para um único workspace.
/// Permite que consumidores externos (ex.: SqlCipherLocalStore) usem o mesmo
/// banco SQLCipher sem reabrir a chave do vault — <see cref="IDbConnectionFactory"/>
/// permanece internal ao assembly Sync.
/// </summary>
public sealed class WorkspaceContext
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ISyncClient SyncClient { get; }

    internal WorkspaceContext(ISyncClient syncClient, IDbConnectionFactory connectionFactory)
    {
        SyncClient = syncClient;
        _connectionFactory = connectionFactory;
    }

    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
        => _connectionFactory.OpenAsync(ct);
}
