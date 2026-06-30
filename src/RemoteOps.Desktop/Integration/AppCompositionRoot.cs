using Microsoft.Extensions.DependencyInjection;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.MikroTik;
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
    internal static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Infraestrutura local
        services.AddSingleton<ILocalStore, InMemoryLocalStore>();

        // Segurança / vault (ADR-003)
        services.AddSingleton<ILocalKeyProtector, DpapiKeyProtector>();
        services.AddSingleton<IWorkspaceKeyStore, InMemoryWorkspaceKeyStore>();
        services.AddSingleton<IWorkspaceKeyRing, WorkspaceKeyRing>();
        services.AddSingleton<ICredentialStore, InMemoryCredentialStore>();
        services.AddSingleton<IVaultAuditSink>(NullVaultAuditSink.Instance);
        services.AddSingleton<CredentialVault>();
        services.AddSingleton<IVault>(sp => sp.GetRequiredService<CredentialVault>());
        services.AddSingleton<ICredentialVault>(sp => sp.GetRequiredService<CredentialVault>());

        // Adaptadores Desktop→Terminal (ADR-011)
        services.AddSingleton<ITerminalSecurityContext, AppTerminalSecurityContext>();
        services.AddSingleton<IEndpointResolver, LocalStoreEndpointResolver>();
        services.AddSingleton<ICredentialRefResolver, StoreCredentialRefResolver>();
        services.AddSingleton<ITerminalAuditSink, StructuredTerminalAuditSink>();
        services.AddSingleton<IHostKeyConfirmation, ModalHostKeyConfirmation>();
        services.AddSingleton<ITelnetConsentProvider, ModalTelnetConsentProvider>();

        // Provedores de sessão terminal (chaveados por protocolo — ADR-009)
        services.AddKeyedSingleton<ITerminalSessionProvider, SshSessionProvider>(RemoteProtocol.Ssh);
        services.AddKeyedSingleton<ITerminalSessionProvider, TelnetSessionProvider>(RemoteProtocol.Telnet);

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
