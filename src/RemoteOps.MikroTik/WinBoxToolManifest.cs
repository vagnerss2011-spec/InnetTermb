using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteOps.MikroTik;

public sealed class WinBoxToolManifest
{
    public string Tool { get; init; } = "winbox";
    public string Version { get; init; } = string.Empty;
    public string Vendor { get; init; } = "MikroTik";
    public string File { get; init; } = "winbox64.exe";
    public string Sha256 { get; init; } = string.Empty;
    public DateTimeOffset ApprovedAt { get; init; }
    public string ApprovedBy { get; init; } = string.Empty;
    public string? Notes { get; init; }

    public static WinBoxToolManifest LoadFromFile(string manifestPath)
    {
        var json = System.IO.File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<WinBoxToolManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Invalid manifest file.");
    }

    public HashValidationResult ValidateExecutable(string executablePath)
    {
        if (!System.IO.File.Exists(executablePath))
            return HashValidationResult.Missing(executablePath);

        using var stream = System.IO.File.OpenRead(executablePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var expected = Sha256.ToLowerInvariant();

        return hash == expected
            ? HashValidationResult.Valid(hash, Version)
            : HashValidationResult.Mismatch(hash, expected);
    }
}

public sealed record HashValidationResult(bool IsValid, string ActualHash, string? MatchedVersion, string? Error)
{
    public static HashValidationResult Valid(string hash, string version) =>
        new(true, hash, version, null);

    public static HashValidationResult Missing(string path) =>
        new(false, string.Empty, null, $"Executable not found: {path}");

    public static HashValidationResult Mismatch(string actual, string expected) =>
        new(false, actual, null, $"SHA-256 mismatch. Expected {expected[..8]}…, got {actual[..8]}…");
}
