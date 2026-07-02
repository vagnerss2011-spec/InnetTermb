namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Combina flags do usuário (settings persistidas) com a variável de ambiente
/// (<see cref="EnvironmentFeatureFlags"/>). O ambiente é override FORTE: o que o env
/// habilitou nunca é desabilitado por settings (garante paridade com CI/operação).
/// Recarrega o store a cada consulta para refletir mudanças salvas sem reiniciar.
/// </summary>
public sealed class CompositeFeatureFlags : IFeatureFlags
{
    private readonly ISettingsStore _store;
    private readonly IFeatureFlags _env;

    public CompositeFeatureFlags(ISettingsStore store, IFeatureFlags env)
    {
        _store = store;
        _env = env;
    }

    public bool IsEnabled(string flagName)
    {
        if (_env.IsEnabled(flagName))
        {
            return true;
        }

        AppSettings settings = _store.Load();
        return settings.Flags.TryGetValue(flagName, out bool enabled) && enabled;
    }
}
