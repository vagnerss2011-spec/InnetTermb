using RemoteOps.Sync;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Monta um <see cref="LocalSyncClient"/> para testes, usando vault fake
/// e diretório temporário isolado por instância.
/// Análogo a VaultTestContext nos testes de Security.
/// </summary>
internal sealed class SyncTestContext : IDisposable
{
    private readonly string _dir;

    public LocalSyncClient Client { get; }
    public FakeCredentialVault Vault { get; }
    public LocalSyncClientFactory Factory { get; }
    public string DbPath { get; }
    public string KeyRefPath { get; }

    private SyncTestContext(
        LocalSyncClient client,
        FakeCredentialVault vault,
        LocalSyncClientFactory factory,
        string dir,
        string dbPath,
        string keyRefPath)
    {
        Client = client;
        Vault = vault;
        Factory = factory;
        _dir = dir;
        DbPath = dbPath;
        KeyRefPath = keyRefPath;
    }

    public static async Task<SyncTestContext> CreateAsync(string workspaceId = "ws-test")
    {
        string dir = Path.Combine(Path.GetTempPath(), "remoteops-sync-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        var vault = new FakeCredentialVault();
        var factory = new LocalSyncClientFactory(vault, dir);
        LocalSyncClient client = await factory.CreateForWorkspaceAsync(workspaceId);

        return new SyncTestContext(
            client, vault, factory, dir,
            factory.DbPath(workspaceId),
            factory.KeyRefPath(workspaceId));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }
}
