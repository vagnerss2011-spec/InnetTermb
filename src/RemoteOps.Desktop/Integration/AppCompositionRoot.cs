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
    /// <summary>
    /// Escopo das ENTIDADES no banco local (<c>assets.workspace_id</c>) — e não a identidade do
    /// cofre.
    ///
    /// <para><b>Fica <c>"ws-local"</c> inclusive no banco do TIME</b>, de propósito: o
    /// <c>SqlCipherLocalStore</c> consulta <c>WHERE workspace_id = $wid</c> e o valor viaja DENTRO
    /// do <c>data_json</c> de cada entidade. Dono e colega precisam calcular a MESMA string, senão a
    /// lista do outro fica VAZIA — em silêncio. Derivar essa string do workspace de servidor
    /// funcionaria só enquanto todo cliente a calculasse igual: uma falha silenciosa nova, de
    /// graça. Quem separa os dois acervos é o ARQUIVO do banco, que é outro.</para>
    /// </summary>
    private const string DefaultWorkspaceId = "ws-local";

    /// <summary>
    /// Caminho in-memory (testes/smoke): registra `InMemoryLocalStore` e um
    /// `CredentialVault` volátil construído pelo container. Escopo pessoal, como o app de hoje.
    /// </summary>
    internal static ServiceProvider Build() =>
        BuildInternal(store: null, vault: null, Account.SessionVaultScope.Personal);

    /// <summary>
    /// Caminho de produção (INT-3): recebe o `CredentialVault` (DPAPI/FileVaultStore)
    /// e o `SqlCipherLocalStore` já inicializados de forma assíncrona em `App.OnStartup`
    /// — DI é síncrono e não pode abrir o workspace/vault. Ambos entram como instâncias.
    /// </summary>
    /// <param name="scope">
    /// Em qual cofre esta sessão escreve. Entra como VALOR, decidido no boot: um escopo consultado
    /// sob demanda poderia responder coisas diferentes a dois ViewModels da mesma janela, e o
    /// desfecho seria a senha do cliente gravada no cofre pessoal — sem erro nenhum na tela.
    /// </param>
    internal static ServiceProvider Build(
        CredentialVault vault, ILocalStore store, Account.SessionVaultScope scope)
        => BuildInternal(store, vault, scope);

    private static ServiceProvider BuildInternal(
        ILocalStore? store, CredentialVault? vault, Account.SessionVaultScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
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

        // Credenciais "inline" (senha só deste device, cifrada no cofre, escondida do Keychain).
        // O cofre vem do ESCOPO da sessão, e não mais por parâmetro do chamador: duas fontes para a
        // mesma verdade é como um lado acaba gravando no cofre errado enquanto o outro acerta.
        services.AddSingleton<Credentials.IInlineCredentialService>(sp => new Credentials.InlineCredentialService(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<IVault>(),
            scope.VaultWorkspaceId));

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
            sp.GetRequiredService<Sessions.IExternalTerminalLauncher>(),
            sp.GetRequiredService<ILocalStore>()));

        services.AddSingleton<ViewModels.HostsViewModel>(sp => new ViewModels.HostsViewModel(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<Sessions.SessionLauncher>(),
            DefaultWorkspaceId,
            sp.GetRequiredService<Credentials.IInlineCredentialService>()));
        // ⚠️ DOIS escopos, e eles NÃO são o mesmo: o 3º argumento é o do STORE (onde a lista de
        // credenciais é indexada, igual em todo cliente) e o 4º é o do COFRE (onde o envelope da
        // senha é selado). Antes eram uma variável só — e é essa conflação que faria a senha do
        // cliente do time nascer no cofre pessoal do operador.
        services.AddSingleton<ViewModels.KeychainViewModel>(sp => new ViewModels.KeychainViewModel(
            sp.GetRequiredService<ILocalStore>(),
            sp.GetRequiredService<RemoteOps.Security.Vault.IVault>(),
            DefaultWorkspaceId,
            scope.VaultWorkspaceId));
        // Indicador de sync + "Sincronizar agora" (Fase 2, item B). Nasce SEM controlador (offline —
        // comando desabilitado); o App liga o controlador quando a sessão de sync sobe.
        services.AddSingleton<ViewModels.SyncStatusViewModel>(_ => new ViewModels.SyncStatusViewModel());

        // Aviso discreto de versão nova na barra de status. GetService (não Required): sem serviço de
        // update — build rodando fora do pacote instalado — a VM nasce inerte e o indicador nunca acende.
        services.AddSingleton<ViewModels.UpdateNotificationViewModel>(sp =>
            new ViewModels.UpdateNotificationViewModel(sp.GetService<IUpdateService>()));

        services.AddSingleton<ViewModels.BrowserViewModel>(sp => new ViewModels.BrowserViewModel(
            sp.GetRequiredService<ViewModels.HostsViewModel>(),
            sp.GetRequiredService<ViewModels.KeychainViewModel>(),
            sp.GetRequiredService<ViewModels.LogsViewModel>(),
            sp.GetService<Changelog.IChangelogSource>(),
            sp.GetService<ISettingsStore>(),
            sp.GetRequiredService<ViewModels.SyncStatusViewModel>(),
            sp.GetRequiredService<ViewModels.UpdateNotificationViewModel>()));
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
