namespace RemoteOps.Desktop.Infrastructure;

public interface IFeatureFlags
{
    bool IsEnabled(string flagName);
}

/// <summary>Nomes de feature flags conhecidos pelo Desktop.</summary>
public static class FeatureFlagNames
{
    /// <summary>Habilita a sessão RDP real (MSTSCAX). Default OFF até o MVP ser validado.</summary>
    public const string RdpEnabled = "rdp.enabled";
}

/// <summary>
/// Lê flags habilitadas da variável de ambiente REMOTEOPS_FEATURE_FLAGS (lista
/// separada por vírgula). Sem a variável (ou vazia), nenhuma flag está habilitada.
/// </summary>
public sealed class EnvironmentFeatureFlags : IFeatureFlags
{
    private readonly HashSet<string> _enabled;

    public EnvironmentFeatureFlags(string? rawFlags = null)
    {
        rawFlags ??= Environment.GetEnvironmentVariable("REMOTEOPS_FEATURE_FLAGS");
        _enabled = new HashSet<string>(
            (rawFlags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled(string flagName) => _enabled.Contains(flagName);
}
