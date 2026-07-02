namespace RemoteOps.Desktop.Infrastructure;

/// <summary>Lê/grava as <see cref="AppSettings"/> do usuário. Nunca lança em Load (defaults em erro).</summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
