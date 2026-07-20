using Velopack;

namespace RemoteOps.Desktop.Update;

/// <summary>
/// Implementação de <see cref="IUpdateService"/> sobre <see cref="UpdateManager"/> do
/// Velopack (ADR-019). Guarda o último <see cref="UpdateInfo"/> retornado por
/// <see cref="CheckForUpdatesAsync"/> para poder aplicá-lo em <see cref="ApplyUpdateAsync"/> —
/// fluxo sequencial check-then-apply, não pensado para checagens concorrentes.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private readonly UpdateManager _manager;
    private readonly IUpdatePolicyFeedSource _policyFeed;
    private UpdateInfo? _lastCheckedUpdateInfo;

    public VelopackUpdateService(UpdateManager manager, IUpdatePolicyFeedSource policyFeed)
    {
        _manager = manager;
        _policyFeed = policyFeed;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        AppVersion current = _manager.CurrentVersion is { } currentVersion
            ? VelopackVersionMapper.ToAppVersion(currentVersion)
            : default;

        _lastCheckedUpdateInfo = await _manager.CheckForUpdatesAsync();
        AppVersion? available = _lastCheckedUpdateInfo?.TargetFullRelease?.Version is { } availableVersion
            ? VelopackVersionMapper.ToAppVersion(availableVersion)
            : null;

        AppVersion? minimumRequired = await _policyFeed.GetMinimumRequiredVersionAsync(ct);

        return UpdateCheckResultFactory.Create(current, available, minimumRequired);
    }

    public async Task ApplyUpdateAsync(UpdateCheckResult update, CancellationToken ct = default)
    {
        if (_lastCheckedUpdateInfo is not { } info)
        {
            // Lança em vez de retornar em silêncio: o chamador trata exceção como "não deu"
            // (TryApplyUpdateAsync → false) e MOSTRA o aviso ao operador. Retornar aqui fazia o caminho
            // de erro parecer sucesso — o operador clicava "sim, atualizar", nada acontecia, e ninguém
            // contava a ele. É o padrão de falha silenciosa que este app já pagou caro.
            throw new InvalidOperationException(
                "Nenhuma atualização verificada para aplicar — refaça a verificação.");
        }

        await _manager.DownloadUpdatesAsync(info, progress: null, ct);
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease, restartArgs: null);
    }
}
