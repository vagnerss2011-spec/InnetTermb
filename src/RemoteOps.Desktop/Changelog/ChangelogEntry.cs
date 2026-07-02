using System.Collections.Generic;

namespace RemoteOps.Desktop.Changelog;

/// <summary>Uma versão do changelog curado do operador.</summary>
public sealed record ChangelogEntry
{
    public string Version { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public IReadOnlyList<string> Highlights { get; init; } = [];
}
