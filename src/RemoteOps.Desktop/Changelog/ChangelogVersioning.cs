using System.Collections.Generic;
using RemoteOps.Desktop.Update;

namespace RemoteOps.Desktop.Changelog;

/// <summary>Comparações SemVer do changelog (DRY entre ChangelogViewModel e BrowserViewModel).</summary>
public static class ChangelogVersioning
{
    /// <summary>True se <paramref name="version"/> é mais nova que <paramref name="lastSeen"/> (ou nunca visto).</summary>
    public static bool IsNewer(string version, string? lastSeen)
    {
        if (!AppVersion.TryParse(version, out AppVersion v))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(lastSeen) || !AppVersion.TryParse(lastSeen, out AppVersion seen))
        {
            return true;
        }

        return v.CompareTo(seen) > 0;
    }

    /// <summary>Maior versão SemVer válida da lista; null se nenhuma parsear.</summary>
    public static string? Latest(IEnumerable<string> versions)
    {
        string? latest = null;
        AppVersion best = default;
        bool has = false;
        foreach (string raw in versions)
        {
            if (!AppVersion.TryParse(raw, out AppVersion v))
            {
                continue;
            }

            if (!has || v.CompareTo(best) > 0)
            {
                best = v;
                latest = raw;
                has = true;
            }
        }

        return latest;
    }
}
