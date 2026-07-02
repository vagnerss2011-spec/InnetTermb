using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.Integration;

/// <summary>
/// Resolve o manifesto do WinBox seguindo a precedência: Configurações (GUI) →
/// variáveis de ambiente → caminho padrão. O hash acompanha a fonte do caminho.
/// </summary>
public static class WinBoxManifestResolver
{
    private const string DefaultExePath = @"C:\Tools\WinBox\winbox64.exe";

    public static WinBoxToolManifest Resolve(string? settingsPath, string? settingsHash, string? envPath, string? envHash)
    {
        string exePath = !string.IsNullOrWhiteSpace(settingsPath) ? settingsPath
            : !string.IsNullOrWhiteSpace(envPath) ? envPath
            : DefaultExePath;

        string? sha = !string.IsNullOrWhiteSpace(settingsHash) ? settingsHash
            : !string.IsNullOrWhiteSpace(envHash) ? envHash
            : null;

        return new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "unknown",
            File = "winbox64.exe",
            Sha256 = sha,
            ExecutablePath = exePath,
        };
    }
}
