namespace RemoteOps.Desktop.Update;

/// <summary>Resultado de uma verificação sob demanda (ADR-019 §2).</summary>
public sealed record UpdateCheckResult(
    AppVersion CurrentVersion,
    AppVersion? AvailableVersion,
    bool UpdateAvailable,
    UpdatePolicyResult Policy);

/// <summary>Combina o resultado do feed de releases com a política de update forçado. Lógica pura.</summary>
public static class UpdateCheckResultFactory
{
    public static UpdateCheckResult Create(
        AppVersion currentVersion, AppVersion? availableVersion, AppVersion? minimumRequiredVersion)
    {
        UpdatePolicyResult policy = UpdatePolicy.Evaluate(currentVersion, minimumRequiredVersion);
        bool updateAvailable = availableVersion is { } available && currentVersion.CompareTo(available) < 0;
        return new UpdateCheckResult(currentVersion, availableVersion, updateAvailable, policy);
    }
}
