using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;

using RemoteOps.Desktop.Account;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Desktop.Update;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Sync;
using RemoteOps.Sync.Remote;
using RemoteOps.Sync.Storage;
using Velopack;

namespace RemoteOps.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private WorkspaceViewModel? _workspaceViewModel;
    private SyncSession? _syncSession;
    private DebouncedAction? _syncReload;
    private SingleInstanceGuard? _singleInstance;
    private AccountSyncCoordinator? _coordinator;
    private VaultRootActivator? _accountActivator;
    private SyncStartContext? _syncContext;
    private AccountConfig? _accountConfig;
    private IntegrityReport? _integrityReport;

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

        // DPI Per-Monitor V2 ANTES de qualquer janela WPF: sem isto o processo fica System-DPI e a
        // UI (terminal, hosts, aba RDP) fica BORRADA ao mover a janela entre monitores de escala
        // diferente (comum em NOC: 100% + 150%/4K). O projeto tem UseWindowsForms=true, então o
        // caminho sancionado é Application.SetHighDpiMode (declarar no manifesto dá o erro WFO0003);
        // ela chama SetProcessDpiAwarenessContext, que o WPF respeita ao criar a 1ª janela.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instância única: se já há uma cópia rodando, acorda a janela dela e encerra ESTA ANTES
        // de abrir o vault/SqlCipher — duas instâncias disputando o mesmo banco local
        // (sync-local.db, Pooling=False) dava erros confusos quando o ícone era clicado 2x.
        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.SignalExistingInstance();
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // async void: sem este try/catch, falha de vault/DB derruba o processo sem
        // nenhum feedback ao operador (crash silencioso no despachante do WPF).
        try
        {
            // A janela de conta abre ANTES da MainWindow, e o ShutdownMode default
            // (OnLastWindowClose) encerra o processo quando a ÚLTIMA janela fecha. Durante o
            // startup a MainWindow ainda não existe, então fechar a AccountWindow — inclusive
            // fechando-a por ter logado com sucesso — deixaria zero janelas e o app "sumiria" no
            // meio do login. Explícito durante o startup; volta ao default assim que a MainWindow
            // está no ar (senão fechar a MainWindow não encerraria mais o app).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteOps");
            Directory.CreateDirectory(dataDir);

            // Vault: envelope encryption protegida por DPAPI (ADR-003).
            // FileVaultStore implementa ICredentialStore e IWorkspaceKeyStore.
            string vaultPath = Path.Combine(dataDir, "vault.json");
            var fileStore = new FileVaultStore(vaultPath);
            var legacyKeyRing = new WorkspaceKeyRing(fileStore, new DpapiKeyProtector());

            // Config de nuvem: Configurações (GUI) primeiro, env var como fallback (compat). Lê o
            // MESMO settings.json que a GUI grava (o JsonSettingsStore default aponta pro mesmo path).
            AppSettings cloudSettings = new JsonSettingsStore().Load();

            // Conta E2EE (Fase 1): resolve a AMK ANTES de montar o cofre — é ela que decide a raiz.
            // Devolve null quando não há conta configurada/logada: aí o cofre segue na raiz DPAPI e
            // o app é exatamente o de hoje (offline-first, ADR-002 — nuvem é opt-in, nunca requisito).
            CredentialVault? activated = await TryActivateAccountAsync(dataDir, fileStore, legacyKeyRing, cloudSettings);
            if (activated is null && await IsVaultAmkRootedAsync(fileStore))
            {
                // Cofre já vinculado a uma conta e sem AMK em mãos (o operador saiu da conta e
                // cancelou o login, ou o cache foi apagado). Não há modo local possível aqui — a
                // chave do banco só abre com a AMK. Dizer isso é MUITO melhor que deixar o startup
                // estourar um erro de cripto genérico logo adiante.
                MessageBox.Show(
                    "Este computador está vinculado a uma conta RemoteOps: seus dados locais estão "
                    + "protegidos pela senha dela.\n\nAbra o RemoteOps de novo e entre na conta para "
                    + "continuar. Sem entrar, não é possível abrir o cofre nem os equipamentos.",
                    "RemoteOps — É preciso entrar na conta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            CredentialVault vault = activated
                ?? new CredentialVault(fileStore, legacyKeyRing, new InMemoryVaultAuditSink());

            // SQLCipher local store (ADR-008): banco criptografado por workspace.
            // Depende do cofre (a chave do banco é um segredo dele), por isso vem DEPOIS da conta:
            // abrir o banco com a raiz antiga e migrar em seguida deixaria a chave ilegível.
            var syncFactory = new LocalSyncClientFactory(vault, dataDir);
            WorkspaceContext ctx = await syncFactory.OpenWorkspaceAsync(AppRuntime.DbWorkspace);

            // Validação de integridade na reabertura (Fase 2, item C): ANTES de confiar no cofre/outbox,
            // roda quick_check + recuperação do WAL + consistência dos cursores. Fail-open — nunca trava
            // o boot; recupera o que der, sinaliza o grave. O resultado vai pros Logs quando a UI subir.
            _integrityReport = await TryValidateIntegrityAsync(
                ctx, syncFactory.DbPath(AppRuntime.DbWorkspace));

            ILocalStore store = new SqlCipherLocalStore(ctx);

            // Composition root (ADR-011): injeta o vault de produção + store SQLCipher
            // e resolve o restante do grafo (adapters de terminal/WinBox, providers, VM).
            _serviceProvider = AppCompositionRoot.Build(vault, store);

            // Update forçado (ADR-019 §3): só existe se REMOTEOPS_UPDATE_FEED_REPO_URL/
            // REMOTEOPS_UPDATE_POLICY_URL estiverem configurados (fail-open sem config).
            // Prompt obrigatório e visível — sem opção de "lembrar depois" — quando a versão
            // instalada está abaixo da mínima exigida pelo feed de política.
            // Não bloquear a abertura da janela indefinidamente: a checagem de update forçado faz um
            // round-trip de rede ao GitHub em TODA inicialização (VelopackUpdateService), sem timeout.
            // Numa rede de campo lenta ou com black-hole/captive-portal, isso deixava o app "congelado"
            // no launch (nenhuma janela por dezenas de segundos). Timeout curto: se estourar, segue
            // fail-open e a janela abre — o update ainda é oferecido pelo prompt do MainWindow.
            var enforceTask = TryEnforceForcedUpdateAsync(_serviceProvider.GetService<IUpdateService>());
            if (await Task.WhenAny(enforceTask, Task.Delay(TimeSpan.FromSeconds(6))) == enforceTask
                && await enforceTask)
            {
                Shutdown();
                return;
            }

            _workspaceViewModel = _serviceProvider.GetRequiredService<WorkspaceViewModel>();

            // 2FA nas Configurações: o VM monta o cliente autenticado sob demanda, reusando os tokens
            // da conta ativa. Lazy → null em modo local (sem conta), e a seção de 2FA fica desabilitada.
            _workspaceViewModel.MfaApiFactory = TryCreateMfaApi;

            // Recarga da lista de hosts quando o sync baixa dados novos (Fase 2). Sem isto, o device B
            // abre com a lista VAZIA: o LoadAsync roda uma vez no Loaded, e nada recarrega quando o
            // primeiro pull materializa os hosts segundos depois. Debounced (300ms) porque um pull
            // grande chega em vários lotes — agrupa tudo numa reconciliação só. A ação marshala pro
            // Dispatcher (o sinal vem da thread de fundo do sync).
            _syncReload = new DebouncedAction(TimeSpan.FromMilliseconds(300), ReconcileHostsFromSyncAsync);

            var window = new MainWindow(
                _workspaceViewModel, store,
                _serviceProvider.GetRequiredService<Credentials.IInlineCredentialService>());
            MainWindow = window;
            window.Show();

            // Janela principal no ar: devolve o comportamento normal — fechar a MainWindow encerra
            // o app (era o default antes do startup mexer nisso pela janela de conta).
            ShutdownMode = ShutdownMode.OnLastWindowClose;

            // Só agora leva o resultado da integridade (item C) ao operador: a janela já está no ar, o
            // Logs existe. Emitir ANTES bloquearia a abertura; aqui o app já abriu (boot nunca trava).
            SurfaceIntegrityReport();

            // A partir daqui, uma 2ª instância que tentar abrir vai só trazer esta janela pra frente.
            _singleInstance.ListenForActivation(() => Dispatcher.Invoke(BringMainWindowToFront));

            // Flush-ao-fechar (Fase 2, item A): logoff/shutdown do Windows encerra o app sem passar
            // por um fechamento "normal" de janela — SessionEnding é a única chance de drenar o outbox
            // antes de o processo morrer. O Alt+F4/fechar-janela cai no OnExit (mais abaixo). Os dois
            // chamam o MESMO flush, guardado pra rodar uma vez só.
            SessionEnding += OnSessionEnding;

            // Cloud sync (ADR-013) atrás da feature flag cloud.sync.enabled (default OFF).
            // OFF (ou config incompleta) → app idêntico ao atual: offline-first, sem rede.
            //
            // Com conta E2EE ativa, o sync sobe pelo coordenador (que sabe o workspace da conta e
            // já deixou os tokens no cofre) — este é o último elo da cadeia AMK → cofre → banco →
            // sync, e por isso vem aqui no fim. Sem conta, o caminho antigo por env var continua
            // valendo (compatibilidade com quem já usava o preview do sync).
            if (_coordinator is not null && _accountConfig is { } account)
            {
                _syncContext = new SyncStartContext(
                    dataDir, ctx, vault, account.CloudUrl, account.DeviceId, fileStore);
                _ = _coordinator.StartSyncAsync();
            }
            else if (TryBuildSyncOptions(dataDir, ctx, vault, out SyncSessionOptions syncOptions))
            {
                StartSyncSession(syncOptions);
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

    /// <summary>
    /// Roda a validação de integridade da reabertura (Fase 2, item C) sem nunca deixar o boot travar.
    /// O próprio <see cref="StartupIntegrityValidator"/> é fail-open; este try/catch é a cinta de
    /// segurança extra (ex.: falha ao instanciar). Devolve <c>null</c> = "não deu pra verificar",
    /// tratado como silêncio — a ausência de checagem nunca pode impedir o app de abrir.
    /// </summary>
    private static async Task<IntegrityReport?> TryValidateIntegrityAsync(WorkspaceContext ctx, string dbPath)
    {
        try
        {
            return await new StartupIntegrityValidator().ValidateAndRecoverAsync(ctx, dbPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Leva o resultado da integridade ao operador: sempre loga (painel de Logs), e mostra um aviso
    /// modal SÓ quando é grave (<see cref="IntegrityReport.ShouldWarnOperator"/>). Roda depois da
    /// janela abrir — o boot já terminou, então nada aqui o trava.
    /// </summary>
    private void SurfaceIntegrityReport()
    {
        if (_integrityReport is not { } report)
        {
            return;
        }

        IUiLogSink? log = _serviceProvider?.GetService<IUiLogSink>();
        foreach (string message in report.Messages)
        {
            log?.Emit($"[integridade] {message}");
        }

        if (report.ShouldWarnOperator)
        {
            MessageBox.Show(
                string.Join("\n\n", report.Messages),
                "RemoteOps — Verificação de integridade",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // Teto do flush-ao-fechar: encurta a janela de perda dos últimos segundos SEM travar o fechamento
    // se a rede estiver fora. O outbox é durável — o que não subir agora sobe no próximo boot.
    private static readonly TimeSpan FlushOnCloseTimeout = TimeSpan.FromSeconds(4);
    private bool _outboxFlushed;

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Logoff/shutdown do Windows: última chance de subir o pendente antes de o processo morrer.
        FlushOutboxOnClose();
    }

    /// <summary>
    /// Drena o outbox uma vez, no fechamento, com teto de tempo (Fase 2, item A). Best-effort e
    /// idempotente: chamado por <see cref="OnSessionEnding"/> E por <see cref="OnExit"/>, mas só o
    /// primeiro efetivamente roda.
    ///
    /// <para><b>Offload pro pool (Task.Run) de propósito:</b> este método bloqueia a UI thread
    /// (GetResult), e o flush encadeia awaits no store SQLCipher que, na UI thread, capturariam o
    /// DispatcherSynchronizationContext — cujas continuações não rodam com a UI thread bloqueada
    /// (deadlock sync-over-async). Rodando o flush dentro de um Task.Run, ele nasce SEM contexto de
    /// sincronização, então nenhuma continuação depende da UI thread; o teto interno do flush garante
    /// que o GetResult retorna rápido mesmo com a rede fora.</para>
    /// </summary>
    private void FlushOutboxOnClose()
    {
        if (_outboxFlushed)
        {
            return;
        }

        _outboxFlushed = true;

        SyncSession? session = _syncSession;
        if (session is null)
        {
            return; // sem sync ativo (offline-first): nada a drenar.
        }

        try
        {
            Task.Run(() => session.FlushOutboxAsync(FlushOnCloseTimeout)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // best-effort: rede fora no fechamento é rotina, não erro.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Flush final do outbox ANTES de descartar a sessão (Fase 2, item A). Se o SessionEnding já
        // rodou (logoff), a guarda evita o segundo flush — nada de esperar o timeout duas vezes.
        FlushOutboxOnClose();

        // Para o timer do debounce antes de derrubar a sessão: um Signal em voo não deve tentar
        // reconciliar sobre uma VM/janela já em processo de encerramento.
        _syncReload?.Dispose();

        try
        {
            _syncSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // shutdown best-effort
        }

        // Zera a AMK que a raiz do cofre mantém viva — o processo pode demorar a morrer.
        _accountActivator?.Dispose();
        _serviceProvider?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    // Traz a janela principal para frente quando uma 2ª instância tentou abrir (roda na UI thread).
    private void BringMainWindowToFront()
    {
        if (MainWindow is not { } w)
        {
            return;
        }

        if (w.WindowState == WindowState.Minimized)
        {
            w.WindowState = WindowState.Normal;
        }

        w.Show();
        w.Activate();
        // Truque padrão para forçar o foco mesmo quando outra janela está ativa.
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
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

    private void StartSyncSession(SyncSessionOptions options)
    {
        _syncSession = SyncSessionFactory.Create(options);
        _syncSession.Orchestrator.StatusChanged += OnSyncStatusChanged;
        // Fase 2: quando um pull grava algo nas tabelas locais, recarrega a lista (debounced +
        // marshalado pro Dispatcher em OnSyncChangesApplied → _syncReload). StatusChanged só mexe numa
        // string; era ele, sozinho, que deixava o device B com a lista vazia até o relaunch.
        _syncSession.Orchestrator.ChangesApplied += OnSyncChangesApplied;

        // Fase 2, item B: liga o "Sincronizar agora" ao orquestrador DESTA sessão (push+pull), o que
        // também habilita os botões no shell (HasCloud vira true). Marshala pro Dispatcher — toca
        // binding de UI. StartSyncSession roda na UI thread hoje, mas o Invoke deixa isso explícito.
        var controller = new OrchestratorSyncController(_syncSession.Orchestrator);
        Dispatcher.Invoke(() => _workspaceViewModel?.Browser.Sync.AttachController(controller));

        OnSyncStatusChanged(_syncSession.Orchestrator.Status);
        _ = StartSyncAsync(_syncSession);
    }

    // Roda na thread de fundo do sync — só sinaliza o debounce (thread-safe); não toca a UI aqui.
    private void OnSyncChangesApplied() => _syncReload?.Signal();

    // Ação do debounce: marshala pro Dispatcher e reconcilia a lista preservando seleção/grupo/filtro.
    // A reconciliação muta ObservableCollections com binding — fora da UI thread seria crash de
    // afinidade de thread do WPF (a causa clássica de queda neste app).
    private async Task ReconcileHostsFromSyncAsync()
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            if (_workspaceViewModel is { } vm)
            {
                await vm.Browser.Hosts.ReconcileFromStoreAsync();
            }
        }).Task.Unwrap();
    }

    /// <summary>
    /// O que o sync da conta precisa e que só existe depois do cofre: o banco local + o cofre
    /// ativado (de onde saem os tokens). Preenchido no fim do OnStartup, lido pelo starter.
    /// A URL/device vêm carimbados aqui (e não relidos do ambiente) pra o sync usar exatamente a
    /// mesma config que a conta usou pra logar.
    /// </summary>
    /// <param name="EnvelopeStore">
    /// O cofre em arquivo, para o transporte de segredos (spec §5) enumerar os envelopes a subir e
    /// gravar os que descem. É o MESMO <see cref="FileVaultStore"/> que o <see cref="CredentialVault"/>
    /// usa — tem que ser, senão o device B gravaria o envelope num cofre que ninguém lê.
    /// </param>
    private sealed record SyncStartContext(
        string DataDir,
        WorkspaceContext Workspace,
        CredentialVault Vault,
        Uri CloudUrl,
        Guid DeviceId,
        FileVaultStore EnvelopeStore);

    /// <summary>
    /// Fluxo de conta E2EE (spec §6), ANTES de o cofre existir — é a AMK que decide a raiz dele.
    ///
    /// <para>Devolve o cofre AMK-rooted, ou <c>null</c> pra "siga no modo local" — que é o caminho
    /// de TODO usuário atual: sem <c>REMOTEOPS_CLOUD_URL</c> configurada não há conta, não há
    /// janela de login e o app é bit a bit o de hoje. Nuvem é opt-in (ADR-002).</para>
    ///
    /// <para><b>Offline-first:</b> servidor fora, operador cancelando o login ou falha de migração
    /// caem todos em <c>null</c> → o app abre com a raiz DPAPI e trabalha local. A única coisa que
    /// se perde é o sync. Nunca há um caminho em que o RemoteOps não abre por causa da nuvem.</para>
    /// </summary>
    private async Task<CredentialVault?> TryActivateAccountAsync(
        string dataDir, FileVaultStore fileStore, WorkspaceKeyRing legacyKeyRing, AppSettings settings)
    {
        if (TryBuildAccountConfig(dataDir, settings) is not { } config)
        {
            return null; // sem nuvem configurada: modo local, exatamente como antes.
        }

        _accountConfig = config;
        var activator = new VaultRootActivator(
            fileStore, legacyKeyRing, Path.Combine(dataDir, "cloud-tokens.tokenref"));
        _accountActivator = activator;

        var amkCache = new DpapiAmkCache(Path.Combine(dataDir, "account.amk"), new DpapiKeyProtector());
        _coordinator = new AccountSyncCoordinator(
            amkCache,
            activator,
            new DelegateSyncStarter(StartAccountSyncAsync),
            AppRuntime.VaultWorkspaces);

        try
        {
            // 1) Relaunch: com cache da AMK, abre sem pedir senha (spec §4.3) e sem tocar a rede.
            if (await _coordinator.TryActivateFromCacheAsync() is not null)
            {
                return activator.Vault;
            }

            // 2) Sem cache: pede login. A janela é modal e ANTES da MainWindow — sem conta não há
            //    raiz pro cofre. Cancelar (X/Esc) devolve null e o app abre local.
            AccountSession? session = ShowAccountWindow(config.CloudUrl, config.DeviceId);
            if (session is null)
            {
                _coordinator = null;
                _accountActivator = null;
                activator.Dispose();
                return null;
            }

            await _coordinator.ActivateFromLoginAsync(session);
            return activator.Vault;
        }
        catch (Exception ex) when (activator.Vault is not null)
        {
            // A ativação passou da troca de raiz e estourou DEPOIS (cache da AMK, tokens). O cofre
            // AMK existe e o vault.json em disco JÁ tem envelopes re-selados sob a AMK — voltar pra
            // raiz DPAPI aqui seria fatal: o cofre legado não abriria o que já migrou, e a chave do
            // banco é um desses segredos (o app morreria com CryptographicException no startup).
            // Segue com a raiz nova; o que se perde é o cache/sync (pede a senha no próximo boot),
            // nunca o cofre.
            _coordinator = null;
            ShowError(
                "Conta ativada, mas com pendências",
                ex,
                "O RemoteOps vai abrir normalmente e seus dados estão intactos, mas talvez peça a "
                + "senha de novo na próxima abertura e fique sem sincronizar.",
                MessageBoxImage.Warning);
            return activator.Vault;
        }
        catch (Exception ex)
        {
            // Estourou ANTES de a raiz virar AMK: o cofre em disco segue como estava, então voltar
            // pro modo local é seguro. Um app que não abre é pior que um app sem sync.
            _coordinator = null;
            _accountActivator = null;
            activator.Dispose();
            ShowError(
                "Não foi possível ativar a conta",
                ex,
                "O RemoteOps vai abrir em modo local (sem sincronização). Seus dados continuam "
                + "neste computador. Tente entrar de novo pelo menu da conta.",
                MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>
    /// O cofre em disco já foi migrado pra raiz AMK?
    ///
    /// <para>Quando já foi, NÃO EXISTE modo local: a chave do banco SQLCipher é um segredo do cofre
    /// (<c>VaultDbKeyProvider</c>) e só a AMK a abre. Sem conta, o app não tem como abrir o banco —
    /// e sem esta checagem ele estouraria um <c>CryptographicException</c> genérico no meio do
    /// startup ("Não foi possível iniciar"), sem dizer ao operador que o que falta é entrar.</para>
    /// </summary>
    private static async Task<bool> IsVaultAmkRootedAsync(FileVaultStore fileStore)
    {
        foreach (string workspaceId in AppRuntime.VaultWorkspaces)
        {
            if (await fileStore.LoadKeyRootingAsync(workspaceId) == VaultKeyRooting.AmkDerived)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cliente autenticado de 2FA pra a tela de Configurações, ou null se não há conta ativa (modo
    /// local). Reusa o token store do coordenador (mesmo cache do sync → refresh coerente). Um
    /// HttpClient novo por abertura é aceitável (ação rara do operador).
    /// </summary>
    private IMfaApi? TryCreateMfaApi()
    {
        if (_coordinator?.ActiveTokenStore is not { } tokens || _accountConfig is not { } config)
        {
            return null;
        }

        var http = new HttpClient { BaseAddress = config.CloudUrl };
        return new MfaApiClient(http, config.DeviceId, tokens);
    }

    /// <summary>Abre a janela de conta (modal) e devolve a sessão autenticada, ou null se cancelou.</summary>
    private static AccountSession? ShowAccountWindow(Uri cloudUrl, Guid deviceId)
    {
        var http = new HttpClient { BaseAddress = cloudUrl };
        var authenticator = new E2eeAccountAuthenticator(
            new AccountApiClient(http), deviceId, Environment.MachineName);
        var viewModel = new AccountViewModel(authenticator);
        var window = new AccountWindow(viewModel);

        return window.ShowDialog() == true ? viewModel.TakeSession() : null;
    }

    /// <summary>
    /// Liga o SyncSession da conta. O coordenador chama isto no fim do OnStartup, quando o banco já
    /// existe — daí ler o <see cref="_syncContext"/> em vez de recebê-lo pronto: na hora em que este
    /// starter é CONSTRUÍDO (antes do cofre), o banco ainda não existe.
    /// </summary>
    private Task StartAccountSyncAsync(string workspaceId, CancellationToken ct)
    {
        // Guarda de ordem: sem contexto não há banco pra sincronizar. Devolver "não iniciou" é o
        // certo — o coordenador trata como sync indisponível (o app abre), nunca como erro fatal.
        if (_syncContext is not { } context)
        {
            throw new InvalidOperationException(
                "O sync da conta precisa do banco local, que só existe depois do cofre ativado.");
        }

        StartSyncSession(new SyncSessionOptions
        {
            Workspace = context.Workspace,
            WorkspaceId = workspaceId,
            CloudBaseUrl = context.CloudUrl,
            DeviceId = context.DeviceId,
            Vault = context.Vault,
            TokenRefPath = Path.Combine(context.DataDir, "cloud-tokens.tokenref"),

            // Transporte de segredos (spec §5): só existe NESTE caminho, o da conta E2EE — é o único
            // em que o cofre está enraizado na AMK, portanto o único em que um envelope subido faz
            // sentido pra outro device. O escopo é o workspace das CREDENCIAIS: a chave do banco
            // (AppRuntime.DbWorkspace) e os tokens (workspace = GUID do servidor) também moram no
            // cofre e não podem sair daqui nunca.
            EnvelopeStore = context.EnvelopeStore,
            VaultWorkspaceId = AppRuntime.CredentialsWorkspace,
        });
        return Task.CompletedTask;
    }

    /// <summary>Config da conta E2EE resolvida do ambiente.</summary>
    private sealed record AccountConfig(Uri CloudUrl, Guid DeviceId);

    /// <summary>
    /// Config mínima pra existir CONTA: a MESMA flag opt-in do sync (<c>cloud.sync.enabled</c>) +
    /// a URL do Cloud (https). Sem as duas não há login — e o app segue local, que é o default de
    /// quem nunca configurou nuvem.
    ///
    /// <para>A flag entra aqui de propósito, e não só a URL: quem já tinha
    /// <c>REMOTEOPS_CLOUD_URL</c> apontada mas o sync desligado não pode passar a levar uma janela
    /// de login na cara ao atualizar. Nuvem é opt-in (ADR-002) e continua sendo — a Fase 1 não muda
    /// quem entra nela, só o que acontece depois.</para>
    ///
    /// <para>HTTPS obrigatório pelo mesmo motivo do sync (ADR-013): authHash e tokens não trafegam
    /// em claro. URL http:// não "avisa e segue" — simplesmente não liga a conta (fail-closed no
    /// transporte, fail-open no app).</para>
    /// </summary>
    private static AccountConfig? TryBuildAccountConfig(string dataDir, AppSettings settings)
    {
        // Configurações (GUI) primeiro, env var como fallback — regra única e testada em CloudConfig.
        (bool enabled, Uri? url) = CloudConfig.Resolve(settings, Environment.GetEnvironmentVariable);
        if (!enabled || url is null)
        {
            return null;
        }

        return new AccountConfig(url, GetOrCreateDeviceId(dataDir));
    }

    private void OnSyncStatusChanged(SyncStatus status)
    {
        WorkspaceViewModel? vm = _workspaceViewModel;
        if (vm is null)
        {
            return;
        }

        // Marshala pro Dispatcher: o StatusChanged vem da thread de fundo do sync e aqui se toca
        // binding de UI (o indicador de status do shell). Atualiza a string legada (back-compat) E o
        // indicador rico da Fase 2 item B.
        Dispatcher.Invoke(() =>
        {
            vm.SyncStatus = FormatStatus(status);
            vm.Browser.Sync.Apply(status);
        });
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
