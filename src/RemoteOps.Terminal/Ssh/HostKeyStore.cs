using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Cache de host keys conhecidas e confiadas (TOFU — Trust On First Use), persistido em
/// <c>%AppData%\RemoteOps\known_hosts.json</c>. Antes era só em memória: a cada reinício do
/// app todo host virava "desconhecido" e o operador tinha que reconfirmar a host key — e a
/// detecção de key alterada nunca disparava entre sessões.
/// </summary>
internal sealed class HostKeyStore
{
    private readonly ConcurrentDictionary<string, string> _trusted;
    private readonly string? _path;
    private readonly object _saveLock = new();

    public HostKeyStore() : this(DefaultPath()) { }

    /// <summary>path null = puramente em memória (testes que não querem tocar disco).</summary>
    public HostKeyStore(string? path)
    {
        _path = path;
        _trusted = Load(path);
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteOps",
        "known_hosts.json");

    public bool IsKnown(string host, string fingerprintHex) =>
        _trusted.TryGetValue(host, out var known) && known == fingerprintHex;

    public bool HasAnyKey(string host) => _trusted.ContainsKey(host);

    public void Trust(string host, string fingerprintHex)
    {
        _trusted[host] = fingerprintHex;
        Save();
    }

    private static ConcurrentDictionary<string, string> Load(string? path)
    {
        var dict = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return dict;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded is not null)
            {
                foreach (var kv in loaded)
                {
                    dict[kv.Key] = kv.Value;
                }
            }
        }
        catch (JsonException)
        {
            // Arquivo corrompido → começa vazio (fail-safe, não quebra a conexão).
        }
        catch (IOException)
        {
        }

        return dict;
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(_path))
        {
            return;
        }

        try
        {
            lock (_saveLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, string>(_trusted)));
            }
        }
        catch (IOException)
        {
            // Best-effort: falha de disco não deve derrubar a sessão SSH.
        }
    }
}
