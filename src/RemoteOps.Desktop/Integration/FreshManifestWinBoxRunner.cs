using System;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.Integration;

/// <summary>
/// Decorator de <see cref="IWinBoxRunner"/> que reconstrói o manifesto (caminho +
/// SHA-256) a cada launch. Antes, o WinBoxToolManifest era singleton materializado
/// no startup: configurar o WinBox em Configurações → Ferramentas externas só valia
/// após reiniciar o app (o launch usava o manifesto stale e falhava fail-closed).
/// </summary>
public sealed class FreshManifestWinBoxRunner : IWinBoxRunner
{
    private readonly Func<WinBoxToolManifest> _manifestFactory;
    private readonly Func<WinBoxToolManifest, IWinBoxRunner> _runnerFactory;

    public FreshManifestWinBoxRunner(
        Func<WinBoxToolManifest> manifestFactory,
        Func<WinBoxToolManifest, IWinBoxRunner> runnerFactory)
    {
        _manifestFactory = manifestFactory;
        _runnerFactory = runnerFactory;
    }

    public Task<string> LaunchAsync(ExternalToolLaunchRequest request, CancellationToken ct = default)
        => _runnerFactory(_manifestFactory()).LaunchAsync(request, ct);
}
