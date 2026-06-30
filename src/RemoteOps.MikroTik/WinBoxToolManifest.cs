using System.Security.Cryptography;

namespace RemoteOps.MikroTik;

public sealed class WinBoxToolManifest
{
    public required string Tool { get; init; }
    public required string Version { get; init; }
    public required string File { get; init; }

    // Null or invalid sha256 → fail-closed in Validate()
    public string? Sha256 { get; init; }

    public required string ExecutablePath { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }

    // sha256 ausente ou inválido → execução bloqueada (fail-closed); nunca NullReferenceException.
    public void Validate()
    {
        if (!IsValidSha256Hex(Sha256))
            throw new WinBoxValidationException(
                "Manifesto WinBox sem sha256 válido — execução bloqueada (fail-closed).");

        if (!System.IO.File.Exists(ExecutablePath))
            throw new WinBoxValidationException(
                "Executável WinBox não encontrado no caminho configurado — execução bloqueada.");

        var actualHash = ComputeSha256(ExecutablePath);
        if (!string.Equals(actualHash, Sha256, StringComparison.OrdinalIgnoreCase))
            throw new WinBoxValidationException(
                "Hash SHA-256 do WinBox não confere com o manifesto aprovado — execução bloqueada.");
    }

    private static bool IsValidSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(static c => char.IsAsciiHexDigit(c));

    private static string ComputeSha256(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
