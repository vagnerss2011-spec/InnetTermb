namespace RemoteOps.Desktop.Update;

/// <summary>
/// Resolve o feed de releases do Velopack. Embute o repositório PÚBLICO de releases como padrão
/// para que o auto-update funcione no app instalado sem configuração e sem token: o código-fonte
/// fica no repo privado <c>InnetTermb</c>, mas os instaladores + feed (<c>releases.win.json</c>)
/// são publicados/espelhados no repo público <c>InnetTermb-releases</c>, que o <c>GithubSource</c>
/// lê anonimamente. Apontar o feed para o repo PRIVADO fazia o check devolver 404 (sem token) e o
/// auto-update nunca rodava. A env var REMOTEOPS_UPDATE_FEED_REPO_URL ainda sobrescreve (staging).
/// </summary>
public static class UpdateFeedConfig
{
    public const string DefaultRepoUrl = "https://github.com/vagnerss2011-spec/InnetTermb-releases";

    public static string ResolveRepoUrl(string? envValue)
        => string.IsNullOrWhiteSpace(envValue) ? DefaultRepoUrl : envValue.Trim();
}
