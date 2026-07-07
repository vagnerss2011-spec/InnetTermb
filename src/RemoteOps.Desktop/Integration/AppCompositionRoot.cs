using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.NDesk;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.Update;
using RemoteOps.MikroTik;
using RemoteOps.Rdp;
using RemoteOps.Security;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;
using RemoteOps.Terminal;
using RemoteOps.Terminal.Ssh;
using RemoteOps.Terminal.Telnet;
using Velopack;
using Velopack.Sources;

namespace RemoteOps.Desktop.Integration;

internal static class AppCompositionRoot
{
    // Workspace único local (Fase 1 — sem multi-workspace na UI ainda).
    private const string DefaultWorkspaceId = "ws-local";

    /// <summary>
    /// Caminho in-memory (testes/smoke): registra `InMemoryLocalStore` e um
    /// `CredentialVault` volátil construído pelo container.
    /// </summary>
    internal static ServiceProvider Build() => BuildInternal(store: null, vault: null);

    /// <summary>
    /// Caminho de produção (INT-3): recebe o `CredentialVault` (DPAPI/FileVaultStore)
    /// e o `SqlCipherLocalStore` já inicializados de forma assíncrona em `App.OnStartup`
    /// — DI é síncrono e não pode abrir o workspace/vault. Ambos entram como instâncias.
    /// </summary>
    internal static ServiceProvider Build(CredentialVault vault, ILocalStore store)
        => BuildInternal(store, vault);

    private static ServiceProvider BuildInternal(ILocalStore? store, CredentialVault? vault)
    {
        var services = new ServiceCollection();

        // Infraestrutura local
        if (store is null)
            services.AddSingleton<ILocalStore, InMemoryLocalStore>();
        else
            services.AddSingleton(store);

        // Segurança / vault (ADR-003)
        if (vault is null)
        {
            // Sub-grafo volátil — só no caminho in-memory (testes).
            services.AddSingleton<ILocalKeyProtector, DpapiKeyProtector>();
            services.AddSingleton<IWorkspaceKeyStore, InMemoryWorkspaceKeyStore>();
            services.AddSingleton<IWorkspaceKeyRing, WorkspaceKeyRing>();
            services.AddSingleton<ICredentialStore, InMemoryCredentialStore>();
            services.AddSingleton<IVaultAuditSink>(NullVaultAuditSink.Instance);
            services.AddSingleton<CredentialVault>();
        }
        else
        {
            services.AddSingleton(vault);
        }

        services.AddSingleton<IVault>(sp => sp.GetRequiredService<CredentialVault>());
        services.AddSingleton<ICredentialVault>(sp => sp.GetRequiredService<CredentialVault>());

        // Adaptadores Desktop→Terminal (ADR-011)
        services.AddSingleton<ITerminalSecurityContext, AppTerminalSecurityContext>();
        services.AddSingleton<IEndpointResolver, LocalStoreEndpointResolver>();
        services.AddSingleton<ICredentialRefResolver, StoreCredentialRefResolver>();
        services.AddSingleton<ITerminalAuditSink, StructuredTerminalAuditSink>();
        services.AddSingleton<IHostKeyConfirmation, ModalHostKeyConfirmation>();
        services.AddSingleton<ITelnetConsentProvider, ModalTelnetConsentProvider>();

        // Settings persistidas + feature flags (settings OU env; env é override forte)
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IFeatureFlags>(sp => new CompositeFeatureFlags(
            sp.GetRequiredService<ISettingsStore>(),
            new EnvironmentFeatureFlags()));

        // Adaptadores Desktop→RDP (ADR-014)
        services.AddSingleton<IRdpEndpointResolver, LocalStoreRdpEndpointResolver>();
        services.AddSingleton<IRdpCredentialRefResolver, LocalStoreRdpCredentialRefResolver>();
        services.AddSingleton<IRdpCredentialResolver, RdpCredentialResolver>();
        services.AddSingleton<IRdpAuditSink, StructuredRdpAuditSink>();
        services.AddSingleton<IRdpSecurityContext, AppTerminalSecurityContext>();

        // Adaptador Desktop→NDesk (fake loopback — troca pelo broker real da Frente 3 vem depois via DI)
        services.AddSingleton<INDeskBrokerClient, LoopbackNDeskBrokerClient>();

        // Provedores de sessão terminal (chaveados por protocolo — ADR-009)
        services.AddKeyedSingleton<ITerminalSessionProvider, SshSessionProvider>(RemoteProtocol.Ssh);
        services.AddKeyedSingleton<ITerminalSessionProvider, TelnetSessionProvider>(RemoteProtocol.Telnet);
        services.AddKeyedSingleton<IRdpSessionProvider, RdpSessionProvider>(RemoteProtocol.Rdp);

        // WinBox / MikroTik (ADR-006)
        services.AddSingleton<IWinBoxAuditSink, StructuredWinBoxAuditSink>();
        services.AddSingleton<IWinBoxCredentialResolver, StoreWinBoxCredentialResolver>();
        services.AddSingleton<IWinBoxPolicyProvider>(_ => new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig()));
        // Manifesto reconstruído a CADA launch (FreshManifestWinBoxRunner): configurar o
        // WinBox em Configurações → Ferramentas externas vale sem reiniciar o app. O
        // manifesto singleton stale era a causa do "clico em Abrir WinBox e nada acontece".
        services.AddSingleton<IWinBoxRunner>(sp => new FreshManifestWinBoxRunner(
            manifestFactory: () => BuildWinBoxManifest(sp),
            runnerFactory: manifest => WinBoxRunner.Create(
                manifest,
                sp.GetRequiredService<IWinBoxPolicyProvider>(),
                sp.GetRequiredService<IWinBoxAuditSink>(),
                sp.GetRequiredService<IWinBoxCredentialResolver>())));

        // Empacotamento/atualização (ADR-019) — sem REMOTEOPS_UPDATE_FEED_REPO_URL/
        // REMOTEOPS_UPDATE_POLICY_URL configurados, não registra nada (fail-open: sem
        // config = sem verificação de update, nunca deixa o app inutilizável).
        RegisterUpdateService(services);

        // ViewModels — shell Termius (Fase 1, Task 12): TabsViewModel/LogsViewModel são
        // singletons compartilhados entre SessionLauncher, BrowserViewModel (via HostsViewModel/
        // LogsViewModel) e WorkspaceViewModel; LogsViewModel também é exposto como IUiLogSink
        // (mesma instância) para os sinks de auditoria emitirem eventos na aba Logs.
        services.AddSingleton<ViewModels.TabsViewModel>();
        services.AddSingleton<ViewModels.LogsViewModel>();
        services.AddSingleton<Infrastructure.IUiLogSink>(sp => sp.GetRequiredService<ViewModels.LogsViewModel>());

        // Changelog (offline) + bug report (mailto) — Fase 1, sem rede.
        services.AddSingleton<Changelog.IChangelogSource, Changelog.EmbeddedChangelogSource>();
        services.AddSingleton<Reporting.IDiagnosticsProvider>(sp => new Reporting.DiagnosticsProvider(
            sp.GetRequiredService<ViewModels.LogsViewModel>(),
            typeof(AppCompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "?",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ReadDeviceId()));
        services.AddSingleton<Reporting.IBugReportComposer, Reporting.MailtoBugReportComposer>();

        // SSH abre num terminal REAL do Windows (ssh.exe), por fora do app — substitui o
        // terminal WebView2 que renderizava escuro/travado em Win11 + NVIDIA (MPO).
        services.AddSingleton<Sessions.IExternalTerminalLauncher, Sessions.WindowsExternalTerminalLauncher>();

        services.AddSingleton<Sessions.SessionLauncher>(sp => new Sessions.SessionLauncher(
            sp.GetRequiredService<ViewModels.TabsViewModel>(),
            sp.GetService<IWinBoxRunner>(),
            sp.GetService<IFeatureFlags>(),
            sp.GetKeyedService<ITerminalSessionProvider>(RemoteProtocol.Ssh),
            sp.GetKeyedService<ITerminalSessionProvider>(RemoteProtocol.Telnet),
            sp.GetKeyedService<IRdpSessionProvider>(RemoteProtocol.Rdp),
            sp.GetService<IRdpCredentialResolver>(),
            sp.GetService<ICredentialRefResolver>(),
            sp.GetRequiredService<Sessions.IExternalTerminalLauncher>()));

        services.AddSingleton<ViewModels.HostsViewModel>(sp => new ViewModels.HostsViewModel(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<Sessions.SessionLauncher>(),
            DefaultWorkspaceId));
        services.AddSingleton<ViewModels.KeychainViewModel>(sp => new ViewModels.KeychainViewModel(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<RemoteOps.Security.Vault.IVault>(),
            DefaultWorkspaceId));
        services.AddSingleton<ViewModels.BrowserViewModel>();
        services.AddSingleton<ViewModels.WorkspaceViewModel>();

        // validateOnBuild: false — ISshConnectionFactory/ITelnetConnectionFactory são internal
        // em RemoteOps.Terminal; os providers públicos usam null como factory (real default).
        // Cobertura equivalente via CompositionRootSmokeTests.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    private static void RegisterUpdateService(ServiceCollection services)
    {
        // Feed embutido por padrão (env sobrescreve) — auto-update funciona no app
        // instalado sem configuração (ADR-019). Ver Update/UpdateFeedConfig.cs.
        string repoUrl = Update.UpdateFeedConfig.ResolveRepoUrl(
            Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_REPO_URL"));

        UpdateManager manager;
        try
        {
            // Token opcional (repo público → null); nunca hardcoded (ADR-019 §4).
            string? accessToken = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_TOKEN");
            var source = new GithubSource(repoUrl, accessToken, prerelease: false);
            manager = new UpdateManager(source);
        }
        catch (InvalidOperationException)
        {
            // App não instalado pelo Velopack (Debug/testes) — sem locator; segue sem update.
            return;
        }

        // Política de update forçado é OPCIONAL: sem REMOTEOPS_UPDATE_POLICY_URL, usa um
        // feed nulo (sem versão mínima) — o update voluntário funciona mesmo assim.
        string? policyUrlRaw = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_POLICY_URL");
        IUpdatePolicyFeedSource policyFeed =
            !string.IsNullOrWhiteSpace(policyUrlRaw)
            && Uri.TryCreate(policyUrlRaw, UriKind.Absolute, out Uri? policyUrl)
                ? new HttpUpdatePolicyFeedSource(new HttpClient(), policyUrl)
                : new Update.NoPolicyFeedSource();

        services.AddSingleton(policyFeed);
        services.AddSingleton<IUpdateService>(sp => new VelopackUpdateService(
            manager, sp.GetRequiredService<IUpdatePolicyFeedSource>()));
    }

    private static string? ReadDeviceId()
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteOps", "device.id");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }

    private static WinBoxToolManifest BuildWinBoxManifest(IServiceProvider sp)
    {
        AppSettings settings = sp.GetRequiredService<ISettingsStore>().Load();
        return WinBoxManifestResolver.Resolve(
            settings.WinBoxExePath,
            settings.WinBoxSha256,
            Environment.GetEnvironmentVariable("WINBOX_EXE_PATH"),
            Environment.GetEnvironmentVariable("WINBOX_SHA256"));
    }
}
