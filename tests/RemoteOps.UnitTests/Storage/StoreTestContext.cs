using System.IO;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Sync;
using RemoteOps.UnitTests.Sync;

namespace RemoteOps.UnitTests.Storage;

/// <summary>
/// Monta um <see cref="SqlCipherLocalStore"/> sobre um diretório temporário isolado.
/// Reutiliza <see cref="FakeCredentialVault"/> e <see cref="LocalSyncClientFactory"/>
/// para permitir "restart" controlado: mesma vault + mesmo dir → mesma chave → mesmo DB.
/// Padrão análogo a <see cref="SyncTestContext"/>.
/// </summary>
internal sealed class StoreTestContext : IDisposable
{
    private readonly string _dir;

    public SqlCipherLocalStore Store { get; }
    public WorkspaceContext Ctx { get; }
    public FakeCredentialVault Vault { get; }
    public LocalSyncClientFactory Factory { get; }
    public string WorkspaceId { get; }

    private StoreTestContext(
        SqlCipherLocalStore store,
        WorkspaceContext ctx,
        FakeCredentialVault vault,
        LocalSyncClientFactory factory,
        string dir,
        string workspaceId)
    {
        Store = store;
        Ctx = ctx;
        Vault = vault;
        Factory = factory;
        _dir = dir;
        WorkspaceId = workspaceId;
    }

    public static async Task<StoreTestContext> CreateAsync(string workspaceId = "ws-store")
    {
        string dir = Path.Combine(Path.GetTempPath(), "remoteops-store-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        var vault = new FakeCredentialVault();
        var factory = new LocalSyncClientFactory(vault, dir);
        WorkspaceContext ctx = await factory.OpenWorkspaceAsync(workspaceId);
        var store = new SqlCipherLocalStore(ctx);

        return new StoreTestContext(store, ctx, vault, factory, dir, workspaceId);
    }

    /// <summary>
    /// Abre um segundo contexto sobre o mesmo banco — simula reinício do app.
    /// Reutiliza a mesma vault instance para recuperar a chave do DB existente.
    /// </summary>
    public async Task<SqlCipherLocalStore> ReopenStoreAsync(CancellationToken ct = default)
    {
        WorkspaceContext ctx2 = await Factory.OpenWorkspaceAsync(WorkspaceId, ct);
        return new SqlCipherLocalStore(ctx2);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
    }
}
