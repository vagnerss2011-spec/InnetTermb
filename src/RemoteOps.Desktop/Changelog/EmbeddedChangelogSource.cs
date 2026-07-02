using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemoteOps.Desktop.Changelog;

/// <summary>Lê o changelog curado embutido no binário (offline, sem rede). Falha → lista vazia.</summary>
public sealed class EmbeddedChangelogSource : IChangelogSource
{
    private const string ResourceName = "operator-changelog.json";
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<ChangelogEntry> Load()
    {
        try
        {
            using Stream? stream = typeof(EmbeddedChangelogSource).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return [];
            }

            ChangelogFile? file = JsonSerializer.Deserialize<ChangelogFile>(stream, Options);
            return file?.Entries ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record ChangelogFile
    {
        public List<ChangelogEntry> Entries { get; init; } = [];
    }
}
