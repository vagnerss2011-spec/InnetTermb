using RemoteOps.Security;
using RemoteOps.Sync.Storage;

namespace RemoteOps.Sync;

/// <summary>
/// Cria instâncias de <see cref="LocalSyncClient"/> com chave do banco protegida
/// pelo vault (DPAPI/envelope, ADR-003).
///
/// Convenção de arquivos por workspace em <paramref name="dbDirectory"/>:
///   <c>sync-{workspaceId}.db</c>    — banco SQLCipher criptografado.
///   <c>sync-{workspaceId}.keyref</c> — envelopeId (referência ao segredo; não o segredo).
/// </summary>
public sealed class LocalSyncClientFactory
{
    private readonly ICredentialVault _vault;
    private readonly string _dbDirectory;

    public LocalSyncClientFactory(ICredentialVault vault, string dbDirectory)
    {
        _vault = vault;
        _dbDirectory = dbDirectory;
    }

    public async Task<LocalSyncClient> CreateForWorkspaceAsync(
        string workspaceId, CancellationToken ct = default)
    {
        WorkspaceContext ctx = await OpenWorkspaceAsync(workspaceId, ct);
        return (LocalSyncClient)ctx.SyncClient;
    }

    /// <summary>
    /// Abre (ou cria) o banco SQLCipher para <paramref name="workspaceId"/> e retorna
    /// um <see cref="WorkspaceContext"/> que expõe o <see cref="ISyncClient"/> e
    /// a abertura de conexões para o mesmo banco — reutilizável por SqlCipherLocalStore.
    /// A chave AES-256 é derivada uma única vez via vault (ADR-003/ADR-008).
    /// </summary>
    public async Task<WorkspaceContext> OpenWorkspaceAsync(
        string workspaceId, CancellationToken ct = default)
    {
        if (workspaceId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("workspaceId contains invalid path characters.", nameof(workspaceId));

        string dbPath = DbPath(workspaceId);
        string keyRefPath = KeyRefPath(workspaceId);

        var keyProvider = new VaultDbKeyProvider(_vault, keyRefPath);
        string hexKey = await keyProvider.GetOrCreateKeyAsync(workspaceId, ct);

        var connFactory = new SqliteConnectionFactory(dbPath, hexKey);
        var syncClient = new LocalSyncClient(connFactory);
        return new WorkspaceContext(syncClient, connFactory);
    }

    /// <summary>Caminho do banco para <paramref name="workspaceId"/> (útil em testes).</summary>
    public string DbPath(string workspaceId) =>
        Path.Combine(_dbDirectory, $"sync-{workspaceId}.db");

    /// <summary>Caminho do arquivo de referência à chave (apenas envelopeId, não o segredo).</summary>
    public string KeyRefPath(string workspaceId) =>
        Path.Combine(_dbDirectory, $"sync-{workspaceId}.keyref");
}
