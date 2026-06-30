using System.Collections.Concurrent;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Cache em memória de host keys conhecidas e confiadas (TOFU — Trust On First Use).
/// Escopo por instância do provider; em produção persistir em armazenamento local seguro.
/// </summary>
internal sealed class HostKeyStore
{
    private readonly ConcurrentDictionary<string, string> _trusted =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsKnown(string host, string fingerprintHex) =>
        _trusted.TryGetValue(host, out var known) && known == fingerprintHex;

    public bool HasAnyKey(string host) => _trusted.ContainsKey(host);

    public void Trust(string host, string fingerprintHex) =>
        _trusted[host] = fingerprintHex;
}
