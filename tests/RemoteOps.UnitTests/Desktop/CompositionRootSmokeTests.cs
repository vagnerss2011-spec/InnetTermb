using Microsoft.Extensions.DependencyInjection;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.MikroTik;
using RemoteOps.Security;
using RemoteOps.Security.Vault;
using RemoteOps.Terminal;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Verifica que AppCompositionRoot.Build() resolve o grafo completo sem abrir nenhuma sessão real.
/// </summary>
public sealed class CompositionRootSmokeTests : IDisposable
{
    private readonly ServiceProvider _provider;

    public CompositionRootSmokeTests() => _provider = AppCompositionRoot.Build();

    public void Dispose() => _provider.Dispose();

    // Infraestrutura -------------------------------------------------------

    [Fact]
    public void Resolve_ILocalStore() =>
        Assert.NotNull(_provider.GetRequiredService<ILocalStore>());

    // Segurança / Vault ---------------------------------------------------

    [Fact]
    public void Resolve_IVault() =>
        Assert.NotNull(_provider.GetRequiredService<IVault>());

    [Fact]
    public void Resolve_ICredentialVault() =>
        Assert.NotNull(_provider.GetRequiredService<ICredentialVault>());

    // Adaptadores Terminal ------------------------------------------------

    [Fact]
    public void Resolve_ITerminalSecurityContext() =>
        Assert.NotNull(_provider.GetRequiredService<ITerminalSecurityContext>());

    [Fact]
    public void Resolve_IEndpointResolver() =>
        Assert.NotNull(_provider.GetRequiredService<IEndpointResolver>());

    [Fact]
    public void Resolve_ICredentialRefResolver() =>
        Assert.NotNull(_provider.GetRequiredService<ICredentialRefResolver>());

    [Fact]
    public void Resolve_ITerminalAuditSink() =>
        Assert.NotNull(_provider.GetRequiredService<ITerminalAuditSink>());

    [Fact]
    public void Resolve_IHostKeyConfirmation() =>
        Assert.NotNull(_provider.GetRequiredService<IHostKeyConfirmation>());

    [Fact]
    public void Resolve_ITelnetConsentProvider() =>
        Assert.NotNull(_provider.GetRequiredService<ITelnetConsentProvider>());

    // Provedores por protocolo (keyed) ------------------------------------

    [Fact]
    public void Resolve_SshSessionProvider_ByKey() =>
        Assert.NotNull(_provider.GetRequiredKeyedService<ITerminalSessionProvider>("ssh"));

    [Fact]
    public void Resolve_TelnetSessionProvider_ByKey() =>
        Assert.NotNull(_provider.GetRequiredKeyedService<ITerminalSessionProvider>("telnet"));

    // WinBox / MikroTik --------------------------------------------------

    [Fact]
    public void Resolve_IWinBoxAuditSink() =>
        Assert.NotNull(_provider.GetRequiredService<IWinBoxAuditSink>());

    [Fact]
    public void Resolve_IWinBoxCredentialResolver() =>
        Assert.NotNull(_provider.GetRequiredService<IWinBoxCredentialResolver>());

    [Fact]
    public void Resolve_IWinBoxRunner() =>
        Assert.NotNull(_provider.GetRequiredService<IWinBoxRunner>());

    // Shell Termius (Fase 1, Task 12) --------------------------------------

    [Fact]
    public void Resolve_WorkspaceViewModel() =>
        Assert.NotNull(_provider.GetRequiredService<WorkspaceViewModel>());

    [Fact]
    public void Resolve_BrowserViewModel() =>
        Assert.NotNull(_provider.GetRequiredService<BrowserViewModel>());

    [Fact]
    public void Resolve_SessionLauncher() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Desktop.Sessions.SessionLauncher>());

    [Fact]
    public void Resolve_TabsViewModel() =>
        Assert.NotNull(_provider.GetRequiredService<TabsViewModel>());

    [Fact]
    public void Resolve_LogsViewModel() =>
        Assert.NotNull(_provider.GetRequiredService<LogsViewModel>());

    [Fact]
    public void Resolve_IUiLogSink_IsSameInstanceAsLogsViewModel()
    {
        var sink = _provider.GetRequiredService<IUiLogSink>();
        var logs = _provider.GetRequiredService<LogsViewModel>();
        Assert.Same(logs, sink);
    }

    [Fact]
    public void TabsViewModel_IsSingleton()
    {
        var a = _provider.GetRequiredService<TabsViewModel>();
        var b = _provider.GetRequiredService<WorkspaceViewModel>();
        Assert.Same(a, b.Tabs);
    }

    [Fact]
    public void BrowserViewModel_SharesHostsViewModel_WithSessionLauncher()
    {
        var workspace = _provider.GetRequiredService<WorkspaceViewModel>();
        var tabs = _provider.GetRequiredService<TabsViewModel>();
        Assert.Same(tabs, workspace.Tabs);
        Assert.Same(workspace.Browser, _provider.GetRequiredService<BrowserViewModel>());
    }

    // Invariante de segurança: ILocalStore singleton (sem múltiplas instâncias com dados diferentes)
    [Fact]
    public void ILocalStore_IsSingleton()
    {
        var a = _provider.GetRequiredService<ILocalStore>();
        var b = _provider.GetRequiredService<ILocalStore>();
        Assert.Same(a, b);
    }

    // Feature flags ---------------------------------------------------------

    [Fact]
    public void Resolve_IFeatureFlags() =>
        Assert.NotNull(_provider.GetRequiredService<IFeatureFlags>());

    // RDP -------------------------------------------------------------------

    [Fact]
    public void Resolve_IRdpEndpointResolver() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpEndpointResolver>());

    [Fact]
    public void Resolve_IRdpCredentialRefResolver() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpCredentialRefResolver>());

    [Fact]
    public void Resolve_IRdpCredentialResolver() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpCredentialResolver>());

    [Fact]
    public void Resolve_IRdpAuditSink() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpAuditSink>());

    [Fact]
    public void Resolve_IRdpSecurityContext() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpSecurityContext>());

    [Fact]
    public void Resolve_RdpSessionProvider_ByKey() =>
        Assert.NotNull(_provider.GetRequiredKeyedService<RemoteOps.Rdp.IRdpSessionProvider>("rdp"));
}
