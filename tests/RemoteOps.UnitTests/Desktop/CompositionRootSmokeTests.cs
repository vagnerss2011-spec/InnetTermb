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

    // ViewModel -----------------------------------------------------------

    [Fact]
    public void Resolve_MainViewModel() =>
        Assert.NotNull(_provider.GetRequiredService<MainViewModel>());

    // Invariante de segurança: ILocalStore singleton (sem múltiplas instâncias com dados diferentes)
    [Fact]
    public void ILocalStore_IsSingleton()
    {
        var a = _provider.GetRequiredService<ILocalStore>();
        var b = _provider.GetRequiredService<ILocalStore>();
        Assert.Same(a, b);
    }
}
