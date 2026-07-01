using Velopack;

namespace RemoteOps.Desktop.Update;

/// <summary>Converte entre o tipo de versão do Velopack e o <see cref="AppVersion"/> interno (lógica pura de política).</summary>
internal static class VelopackVersionMapper
{
    public static AppVersion ToAppVersion(SemanticVersion version) => AppVersion.Parse(version.ToNormalizedString());
}
