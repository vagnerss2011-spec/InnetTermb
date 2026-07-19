using System;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Resolve a configuração de nuvem (ligado? qual servidor?) a partir das <see cref="AppSettings"/>,
/// com as env vars como FALLBACK (compat com quem já usava <c>REMOTEOPS_CLOUD_SYNC_ENABLED</c> /
/// <c>REMOTEOPS_CLOUD_URL</c> antes de existir a UI). Puro/testável — o <c>App</c> injeta
/// <see cref="Environment.GetEnvironmentVariable(string)"/> como o leitor de env.
///
/// <para>Precedência: as Configurações VENCEM (é a fonte nova, editável na GUI); a env var só entra
/// quando o campo correspondente está vazio. URL exige HTTPS (fail-closed, ADR-013): http:// não
/// liga a nuvem, cai pro modo local.</para>
/// </summary>
public static class CloudConfig
{
    public const string EnabledEnvVar = "REMOTEOPS_CLOUD_SYNC_ENABLED";
    public const string UrlEnvVar = "REMOTEOPS_CLOUD_URL";

    /// <summary>(Enabled, Url) — Url é null quando ausente ou não-HTTPS. Enabled sem Url = nuvem não sobe.</summary>
    public static (bool Enabled, Uri? Url) Resolve(AppSettings settings, Func<string, string?> getEnv)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(getEnv);

        // Configurou pela GUI → a GUI manda (inclusive DESLIGAR). Nunca configurou → env como fallback.
        bool enabled = settings.CloudSyncConfigured
            ? settings.CloudSyncEnabled
            : string.Equals(getEnv(EnabledEnvVar), "true", StringComparison.OrdinalIgnoreCase);

        string? raw = !string.IsNullOrWhiteSpace(settings.CloudServerUrl)
            ? settings.CloudServerUrl
            : getEnv(UrlEnvVar);

        Uri? url = null;
        if (!string.IsNullOrWhiteSpace(raw)
            && Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed)
            && parsed.Scheme == Uri.UriSchemeHttps)
        {
            url = parsed;
        }

        return (enabled, url);
    }
}
