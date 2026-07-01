using System.Globalization;

namespace RemoteOps.Desktop.Update;

/// <summary>
/// SemVer 2.0 (subconjunto: major.minor.patch[-prerelease], sem build metadata "+...").
/// Lógica pura, sem dependência do pacote Velopack, para permitir teste isolado da
/// política de atualização forçada (ADR-019).
/// </summary>
public readonly record struct AppVersion : IComparable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }

    private AppVersion(int major, int minor, int patch, string? preRelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
    }

    public static AppVersion Parse(string value)
    {
        if (!TryParse(value, out AppVersion version))
        {
            throw new FormatException($"Versão inválida: '{value}'.");
        }

        return version;
    }

    public static bool TryParse(string? value, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string core = value;
        string? preRelease = null;
        int dashIndex = value.IndexOf('-');
        if (dashIndex >= 0)
        {
            core = value[..dashIndex];
            preRelease = value[(dashIndex + 1)..];
            if (preRelease.Length == 0)
            {
                return false;
            }
        }

        string[] parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParseNonNegativeInt(parts[0], out int major)
            || !TryParseNonNegativeInt(parts[1], out int minor)
            || !TryParseNonNegativeInt(parts[2], out int patch))
        {
            return false;
        }

        version = new AppVersion(major, minor, patch, preRelease);
        return true;
    }

    private static bool TryParseNonNegativeInt(string value, out int result)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    public int CompareTo(AppVersion other)
    {
        int cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;

        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;

        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // SemVer 2.0 §11: versão sem prerelease tem precedência maior que com prerelease.
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        string[] leftIds = left.Split('.');
        string[] rightIds = right.Split('.');
        int count = Math.Min(leftIds.Length, rightIds.Length);

        for (int i = 0; i < count; i++)
        {
            int cmp = CompareIdentifier(leftIds[i], rightIds[i]);
            if (cmp != 0) return cmp;
        }

        return leftIds.Length.CompareTo(rightIds.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out int leftNum);
        bool rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out int rightNum);

        if (leftNumeric && rightNumeric) return leftNum.CompareTo(rightNum);
        if (leftNumeric) return -1; // identificador numérico tem precedência menor que alfanumérico
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    public override string ToString()
        => PreRelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
