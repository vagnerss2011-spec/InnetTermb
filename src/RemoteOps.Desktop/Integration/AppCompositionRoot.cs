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
        services.AddSingleton<WinBoxToolManifest>(BuildWinBoxManifest);
        services.AddSingleton<IWinBoxRunner>(sp => WinBoxRunner.Create(
            sp.GetRequiredService<WinBoxToolManifest>(),
            sp.GetRequiredService<IWinBoxPolicyProvider>(),
            sp.GetRequiredService<IWinBoxAuditSink>(),
            sp.GetRequiredService<IWinBoxCredentialResolver>()));

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

        services.AddSingleton<Sessions.SessionLauncher>(sp => new Sessions.SessionLauncher(
            sp.GetRequiredService<ViewModels.TabsViewModel>(),
            sp.GetService<IWinBoxRunner>(),
            sp.GetService<IFeatureFlags>(),
            sp.GetKeyedService<ITerminalSessionProvider>(RemoteProtocol.Ssh),
            sp.GetKeyedService<ITerminalSessionProvider>(RemoteProtocol.Telnet),
            sp.GetKeyedService<IRdpSessionProvider>(RemoteProtocol.Rdp),
            sp.GetService<IRdpCredentialResolver>()));

        services.AddSingleton<ViewModels.HostsViewModel>(sp => new ViewModels.HostsViewModel(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<Sessions.SessionLauncher>(),
            DefaultWorkspaceId));
        services.AddSingleton<ViewModels.KeychainViewModel>(sp => new ViewModels.KeychainViewModel(
            sp.GetRequiredService<ILocalStore>(),
            DefaultWorkspaceId));
        services.AddSingleton<ViewModels.BrowserViewModel>();
        services.AddSingleton<ViewModels.WorkspaceViewModel>();

        // MainViewModel permanece registrado até a Task 13 (remoção do shell antigo) — ainda
        // coberto por MainViewModel*Tests.
        services.AddSingleton<ViewModels.MainViewModel>();

        // validateOnBuild: false — ISshConnectionFactory/ITelnetConnectionFactory são internal
        // em RemoteOps.Terminal; os providers públicos usam null como factory (real default).
        // Cobertura equivalente via CompositionRootSmokeTests.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }

    private static void RegisterUpdateService(ServiceCollection services)
    {
        string? repoUrl = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_REPO_URL");
        string? policyUrlRaw = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_POLICY_URL");
        if (string.IsNullOrWhiteSpace(repoUrl)
            || string.IsNullOrWhiteSpace(policyUrlRaw)
            || !Uri.TryCreate(policyUrlRaw, UriKind.Absolute, out Uri? policyUrl))
        {
            return;
        }

        UpdateManager manager;
        try
        {
            // Token opcional (repositório privado) — nunca hardcoded, sempre de variável
            // de ambiente/GitHub Environment (ADR-019 §4). null é válido para repositório
            // público.
            string? accessToken = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_TOKEN");
            var source = new GithubSource(repoUrl, accessToken, prerelease: false);
            manager = new UpdateManager(source);
        }
        catch (InvalidOperationException)
        {
            // VelopackApp.Build().Run() não rodou (ex.: execução fora de um app instalado
            // pelo Velopack, como testes) — não há locator disponível; segue sem registrar.
            return;
        }

        services.AddSingleton<IUpdatePolicyFeedSource>(_ => new HttpUpdatePolicyFeedSource(new HttpClient(), policyUrl));
        services.AddSingleton<IUpdateService>(sp => new VelopackUpdateService(
            manager, sp.GetRequiredService<IUpdatePolicyFeedSource>()));
    }

    private static WinBoxToolManifest BuildWinBoxManifest(IServiceProvider _)
    {
        string exePath = Environment.GetEnvironmentVariable("WINBOX_EXE_PATH")
            ?? @"C:\Tools\WinBox\winbox64.exe";
        return new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "unknown",
            File = "winbox64.exe",
            Sha256 = Environment.GetEnvironmentVariable("WINBOX_SHA256"),
            ExecutablePath = exePath,
        };
    }
}
