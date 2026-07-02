using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class ChangelogViewModelTests
{
    private sealed class FakeSource : IChangelogSource
    {
        private readonly IReadOnlyList<ChangelogEntry> _entries;
        public FakeSource(params string[] versions)
            => _entries = versions.Select(v => new ChangelogEntry { Version = v, Date = "2026-01-01", Highlights = new[] { "h" } }).ToList();
        public IReadOnlyList<ChangelogEntry> Load() => _entries;
    }

    private static JsonSettingsStore TempStore()
        => new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json"));

    [Fact]
    public void NeverSeen_AllEntriesNew()
    {
        var vm = new ChangelogViewModel(new FakeSource("1.0.0", "1.1.0"), TempStore());
        Assert.True(vm.HasEntries);
        Assert.All(vm.Entries, e => Assert.True(e.IsNew));
    }

    [Fact]
    public void LastSeen_MarksOnlyNewerAsNew()
    {
        var store = TempStore();
        store.Save(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        var vm = new ChangelogViewModel(new FakeSource("1.0.0", "1.1.0"), store);
        Assert.False(vm.Entries.Single(e => e.Version == "1.0.0").IsNew);
        Assert.True(vm.Entries.Single(e => e.Version == "1.1.0").IsNew);
    }

    [Fact]
    public void MarkAllSeen_PersistsLatestVersion()
    {
        var store = TempStore();
        var vm = new ChangelogViewModel(new FakeSource("1.0.0", "1.1.0"), store);
        vm.MarkAllSeen();
        Assert.Equal("1.1.0", store.Load().LastSeenChangelogVersion);
    }
}
