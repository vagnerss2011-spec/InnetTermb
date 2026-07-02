namespace RemoteOps.Desktop.Update;

/// <summary>
/// Resolve o feed de releases do Velopack. Embute o repositório oficial como padrão para
/// que o auto-update funcione no app instalado sem configuração; a env var
/// REMOTEOPS_UPDATE_FEED_REPO_URL sobrescreve (repo privado/staging).
/// </summary>
public static class UpdateFeedConfig
{
    public const string DefaultRepoUrl = "https://github.com/vagnerss2011-spec/InnetTermb";

    public static string ResolveRepoUrl(string? envValue)
        => string.IsNullOrWhiteSpace(envValue) ? DefaultRepoUrl : envValue.Trim();
}
