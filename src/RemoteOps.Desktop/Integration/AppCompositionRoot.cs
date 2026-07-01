using Microsoft.Extensions.DependencyInjection;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.NDesk;
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

namespace RemoteOps.Desktop.Integration;

internal static class AppCompositionRoot
{
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

        // Feature flags (default OFF — REMOTEOPS_FEATURE_FLAGS env var)
        services.AddSingleton<IFeatureFlags, EnvironmentFeatureFlags>();

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

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();

        // validateOnBuild: false — ISshConnectionFactory/ITelnetConnectionFactory são internal
        // em RemoteOps.Terminal; os providers públicos usam null como factory (real default).
        // Cobertura equivalente via CompositionRootSmokeTests.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
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
