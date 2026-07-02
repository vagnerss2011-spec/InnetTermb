using System;
using System.IO;
using System.Text.Json;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Persiste as settings em <c>%AppData%\RemoteOps\settings.json</c> (mesmo diretório do
/// vault). Load é fail-safe: arquivo ausente ou corrompido devolve os defaults.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonSettingsStore() : this(DefaultPath()) { }

    public JsonSettingsStore(string path) => _path = path;

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteOps",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(_path, json);
    }
}
