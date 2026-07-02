using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed record ChangelogItemViewModel(string Version, string Date, IReadOnlyList<string> Highlights, bool IsNew);

/// <summary>Aba "Novidades": lista as versões curadas, marca as novas desde a última visita.</summary>
public sealed class ChangelogViewModel : BaseViewModel
{
    private readonly ISettingsStore _store;

    public ChangelogViewModel(IChangelogSource source, ISettingsStore store)
    {
        _store = store;
        string? lastSeen = store.Load().LastSeenChangelogVersion;
        foreach (ChangelogEntry e in source.Load())
        {
            Entries.Add(new ChangelogItemViewModel(e.Version, e.Date, e.Highlights, ChangelogVersioning.IsNewer(e.Version, lastSeen)));
        }
    }

    public ObservableCollection<ChangelogItemViewModel> Entries { get; } = [];
    public bool HasEntries => Entries.Count > 0;

    /// <summary>Grava a versão mais recente como "vista" (chamado quando a aba Novidades abre).</summary>
    public void MarkAllSeen()
    {
        string? latest = ChangelogVersioning.Latest(Entries.Select(e => e.Version));
        if (latest is null)
        {
            return;
        }

        AppSettings settings = _store.Load();
        _store.Save(settings with { LastSeenChangelogVersion = latest });
    }
}
