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
    /// Comportamento de hoje, byte a byte: o id serve de nome de arquivo <b>E</b> de identidade do
    /// cofre onde a chave do banco fica guardada.
    /// </summary>
    public Task<WorkspaceContext> OpenWorkspaceAsync(
        string workspaceId, CancellationToken ct = default)
        => OpenWorkspaceAsync(workspaceId, workspaceId, ct);

    /// <summary>
    /// Abre (ou cria) o banco SQLCipher e retorna um <see cref="WorkspaceContext"/> que expõe o
    /// <see cref="ISyncClient"/> e a abertura de conexões para o mesmo banco — reutilizável por
    /// SqlCipherLocalStore. A chave AES-256 é derivada uma única vez via vault (ADR-003/ADR-008).
    /// </summary>
    /// <param name="dbName">
    /// Nome do ARQUIVO (<c>sync-{dbName}.db</c>). Um banco por escopo: o outbox não é escopado, e
    /// com banco único a fila empurraria host do time para o cofre pessoal.
    /// </param>
    /// <param name="dbKeyVaultWorkspaceId">
    /// Onde a chave AES do SQLCipher DESTE banco fica guardada no cofre. É <b>sempre</b>
    /// <c>"local"</c> no app, inclusive para o banco do time: a chave do banco é POR MÁQUINA. Se ela
    /// morasse em <c>"time:{W}"</c>, nasceria <c>WkRootedV1</c> — e o <c>IsSyncable</c> ACEITA
    /// <c>WkRootedV1</c>: a chave do banco de cada máquina subiria para o servidor e desceria para
    /// os colegas. O inverso exato da intenção.
    /// </param>
    public async Task<WorkspaceContext> OpenWorkspaceAsync(
        string dbName, string dbKeyVaultWorkspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbName);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbKeyVaultWorkspaceId);

        // ⚠️ Só o NOME DO ARQUIVO passa por esta guarda. A identidade do cofre pode (e no time,
        // precisa) conter ':', que é caractere inválido em nome de arquivo no Windows — é por isso
        // que as duas deixaram de ser a mesma string.
        if (dbName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("dbName contains invalid path characters.", nameof(dbName));

        string dbPath = DbPath(dbName);
        string keyRefPath = KeyRefPath(dbName);

        var keyProvider = new VaultDbKeyProvider(_vault, keyRefPath);
        string hexKey = await keyProvider.GetOrCreateKeyAsync(dbKeyVaultWorkspaceId, ct);

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
