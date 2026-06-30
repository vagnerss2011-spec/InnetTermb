using System.IO;
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
    public WorkspaceContext Workspace { get; }
    public FakeCredentialVault Vault { get; }
    public LocalSyncClientFactory Factory { get; }
    public string DbPath { get; }
    public string KeyRefPath { get; }

    private SyncTestContext(
        LocalSyncClient client,
        WorkspaceContext workspace,
        FakeCredentialVault vault,
        LocalSyncClientFactory factory,
        string dir,
        string dbPath,
        string keyRefPath)
    {
        Client = client;
        Workspace = workspace;
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
        WorkspaceContext workspace = await factory.OpenWorkspaceAsync(workspaceId);
        var client = (LocalSyncClient)workspace.SyncClient;

        return new SyncTestContext(
            client, workspace, vault, factory, dir,
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
