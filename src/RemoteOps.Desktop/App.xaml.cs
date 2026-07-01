using System.IO;
using System.Windows;
using System.Windows.Threading;

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
        // Rede de segurança: nenhuma exceção não tratada deve derrubar o app sem diálogo
        // (CLAUDE.md — nunca crash silencioso). DispatcherUnhandledException cobre a UI
        // thread depois que o startup termina (ex.: async void em MainWindow.Loaded);
        // AppDomain.UnhandledException é o último recurso para exceções fora dela.
        // Falhas SÍNCRONAS dentro do próprio OnStartup (antes do dispatcher bombear
        // mensagens) não passam por nenhuma delas — por isso o try/catch de OnStartup.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    // Entry point custom (ADR-019): App.xaml não é mais ApplicationDefinition (ver
    // RemoteOps.Desktop.csproj), então este Main() substitui o gerado automaticamente
    // pelo WPF. VelopackApp.Build().Run() precisa ser a primeiríssima instrução — antes
    // de qualquer InitializeComponent()/DI/vault — porque o Setup.exe do Velopack invoca
    // o app com argumentos internos (instalação, pós-update etc.) que só são
    // interceptados aqui, e a própria ferramenta `vpk pack` avisa quando essa chamada não
    // está no início do Main() (validado localmente: o aviso desaparece com este padrão).
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
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
        catch (Exception ex)
        {
            // Qualquer falha de inicialização (DPAPI, SQLCipher nativo, permissão de disco,
            // binding da primeira janela) vira mensagem amigável — o app nunca é utilizável
            // sem vault/store, então encerra em seguida. Ver docs/26-runbook-teste-local.md
            // §Solução de problemas.
            ShowError(
                "Não foi possível iniciar",
                ex,
                "Verifique se o WebView2 Runtime está instalado e se há permissão de escrita " +
                "em %APPDATA%\\RemoteOps. Consulte docs/26-runbook-teste-local.md.",
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Exceção não tratada na UI thread depois do startup (ex.: falha dentro de um
        // "async void" de evento). Mostra o erro e deixa o app continuar — preferível a
        // derrubar uma sessão em andamento sem aviso nenhum.
        ShowError("Erro inesperado", e.Exception, hint: null, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Último recurso: exceção fora da UI thread, quase sempre fatal a esta altura
        // (IsTerminating). Só tentamos avisar antes do processo cair — best effort.
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }

        try
        {
            ShowError("Erro fatal", ex, hint: null, MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Já era o último recurso antes do processo encerrar; nada mais a fazer.
        }
    }

    private static void ShowError(string title, Exception ex, string? hint, MessageBoxImage icon)
    {
        string message = hint is null
            ? $"{ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message}\n\n{hint}";

        MessageBox.Show(message, $"RemoteOps — {title}", MessageBoxButton.OK, icon);
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
