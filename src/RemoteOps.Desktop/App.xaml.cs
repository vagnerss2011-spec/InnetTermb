using System.IO;
using System.Windows;

using Microsoft.Extensions.DependencyInjection;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync;

namespace RemoteOps.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteOps");
        Directory.CreateDirectory(dataDir);

        // Vault: envelope encryption protegida por DPAPI (ADR-003).
        // FileVaultStore implementa ICredentialStore e IWorkspaceKeyStore.
        string vaultPath = Path.Combine(dataDir, "vault.json");
        var fileStore = new FileVaultStore(vaultPath);
        var keyRing = new WorkspaceKeyRing(fileStore, new DpapiKeyProtector());
        var vault = new CredentialVault(fileStore, keyRing, new InMemoryVaultAuditSink());

        // SQLCipher local store (ADR-008): banco criptografado por workspace.
        var syncFactory = new LocalSyncClientFactory(vault, dataDir);
        WorkspaceContext ctx = await syncFactory.OpenWorkspaceAsync("local");
        ILocalStore store = new SqlCipherLocalStore(ctx);

        // Composition root (ADR-011): injeta o vault de produção + store SQLCipher
        // e resolve o restante do grafo (adapters de terminal/WinBox, providers, VM).
        _serviceProvider = AppCompositionRoot.Build(vault, store);
        var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        var window = new MainWindow(viewModel);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
