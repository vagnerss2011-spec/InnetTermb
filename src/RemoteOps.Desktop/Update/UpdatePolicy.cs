namespace RemoteOps.Desktop.Update;

/// <summary>
/// Gate de atualização forçada (ADR-019): quando a versão instalada é menor que a
/// versão mínima exigida (campo do feed/config), o uso do app deve ser bloqueado até
/// atualizar. Lógica pura — sem I/O, sem dependência do Velopack.
/// </summary>
public static class UpdatePolicy
{
    public static UpdatePolicyResult Evaluate(AppVersion currentVersion, AppVersion? minimumRequiredVersion)
    {
        bool mustUpdate = minimumRequiredVersion is { } min && currentVersion.CompareTo(min) < 0;
        return new UpdatePolicyResult(mustUpdate, minimumRequiredVersion);
    }
}

/// <summary>Resultado da avaliação da política de atualização forçada.</summary>
public sealed record UpdatePolicyResult(bool MustUpdate, AppVersion? MinimumRequiredVersion);
