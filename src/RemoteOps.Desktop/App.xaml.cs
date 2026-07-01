using System.IO;
using System.Windows;

using Microsoft.Extensions.DependencyInjection;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Desktop.Update;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;
using Velopack;

namespace RemoteOps.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private MainViewModel? _mainViewModel;
    private SyncSession? _syncSession;

    public App()
    {
        // ADR-019: precisa ser a primeira coisa executada — antes de UI/DI/vault — porque
        // o Setup.exe do Velopack invoca o app com argumentos internos (instalação,
        // pós-update, etc.) que só são interceptados aqui. WPF cria a instância de `App`
        // antes de chamar InitializeComponent()/Run(), então o construtor é o ponto mais
        // cedo disponível sem reescrever o entry point gerado.
        VelopackApp.Build().Run();
    }

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

        // Update forçado (ADR-019 §3): só existe se REMOTEOPS_UPDATE_FEED_REPO_URL/
        // REMOTEOPS_UPDATE_POLICY_URL estiverem configurados (fail-open sem config).
        // Prompt obrigatório e visível — sem opção de "lembrar depois" — quando a versão
        // instalada está abaixo da mínima exigida pelo feed de política.
        if (await TryEnforceForcedUpdateAsync(_serviceProvider.GetService<IUpdateService>()))
        {
            Shutdown();
            return;
        }

        _mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        var window = new MainWindow(_mainViewModel);
        window.Show();

        // Cloud sync (ADR-013) atrás da feature flag cloud.sync.enabled (default OFF).
        // OFF (ou config incompleta) → app idêntico ao atual: offline-first, sem rede.
        if (TryBuildSyncOptions(dataDir, ctx, vault, out SyncSessionOptions syncOptions))
        {
            _syncSession = SyncSessionFactory.Create(syncOptions);
            _syncSession.Orchestrator.StatusChanged += OnSyncStatusChanged;
            OnSyncStatusChanged(_syncSession.Orchestrator.Status);
            _ = StartSyncAsync(_syncSession);
        }
    }

    // Retorna true quando o app deve encerrar (update forçado aplicado/em aplicação).
    // Nunca lança: falha de checagem (rede indisponível, feed de política fora do ar)
    // é fail-open — não trava o operador por causa de uma verificação que não completou.
    private static async Task<bool> TryEnforceForcedUpdateAsync(IUpdateService? updateService)
    {
        if (updateService is null)
        {
            return false;
        }

        UpdateCheckResult check;
        try
        {
            check = await updateService.CheckForUpdatesAsync();
        }
        catch (Exception)
        {
            return false;
        }

        if (!check.Policy.MustUpdate)
        {
            return false;
        }

        MessageBox.Show(
            $"Uma atualização obrigatória está disponível (versão mínima exigida: " +
            $"{check.Policy.MinimumRequiredVersion}). O RemoteOps Desktop será atualizado agora.",
            "Atualização obrigatória",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        await updateService.ApplyUpdateAsync(check);
        return true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _syncSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // shutdown best-effort
        }

        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static async Task StartSyncAsync(SyncSession session)
    {
        try
        {
            await session.StartAsync();
        }
        catch (Exception)
        {
            // Falha ao iniciar o sync não derruba o app; o estado reflete o erro.
        }
    }

    private void OnSyncStatusChanged(SyncStatus status)
    {
        MainViewModel? vm = _mainViewModel;
        if (vm is null)
        {
            return;
        }

        Dispatcher.Invoke(() => vm.SyncStatus = FormatStatus(status));
    }

    private static string FormatStatus(SyncStatus status) => status.State switch
    {
        SyncState.Offline => "Offline",
        SyncState.Syncing => "Sincronizando…",
        SyncState.Synced => status.ConflictCount > 0
            ? $"Sincronizado ({status.ConflictCount} conflito(s))"
            : "Sincronizado",
        SyncState.Error => "Erro de sincronização",
        _ => "Offline",
    };

    // Feature flag + leitura de configuração de ambiente. Retorna false (sem sync) quando a flag está
    // OFF ou a config está incompleta — nunca derruba o app por falta de configuração de nuvem.
    private static bool TryBuildSyncOptions(
        string dataDir, WorkspaceContext ctx, CredentialVault vault, out SyncSessionOptions options)
    {
        options = null!;

        if (!string.Equals(
                Environment.GetEnvironmentVariable("REMOTEOPS_CLOUD_SYNC_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? url = Environment.GetEnvironmentVariable("REMOTEOPS_CLOUD_URL");
        string? workspaceId = Environment.GetEnvironmentVariable("REMOTEOPS_CLOUD_WORKSPACE_ID");
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? baseUrl)
            || baseUrl.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(workspaceId))
        {
            return false; // exige HTTPS — fail-closed: URL http:// não liga o sync (ADR-013)
        }

        options = new SyncSessionOptions
        {
            Workspace = ctx,
            WorkspaceId = workspaceId,
            CloudBaseUrl = baseUrl,
            DeviceId = GetOrCreateDeviceId(dataDir),
            Vault = vault,
            TokenRefPath = Path.Combine(dataDir, "cloud-tokens.tokenref"),
        };
        return true;
    }

    private static Guid GetOrCreateDeviceId(string dataDir)
    {
        string path = Path.Combine(dataDir, "device.id");
        if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out Guid existing))
        {
            return existing;
        }

        var id = Guid.NewGuid();
        File.WriteAllText(path, id.ToString());
        return id;
    }
}
