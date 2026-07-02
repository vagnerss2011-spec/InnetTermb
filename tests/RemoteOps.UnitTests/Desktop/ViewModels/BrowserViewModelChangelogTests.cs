using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class BrowserViewModelChangelogTests
{
    private sealed class FakeSource : IChangelogSource
    {
        private readonly IReadOnlyList<ChangelogEntry> _e;
        public FakeSource(params string[] v) => _e = v.Select(x => new ChangelogEntry { Version = x }).ToList();
        public IReadOnlyList<ChangelogEntry> Load() => _e;
    }

    private static BrowserViewModel Build(JsonSettingsStore store)
    {
        var logs = new LogsViewModel();
        var hosts = new HostsViewModel(new InMemoryLocalStore(), null!, "ws-local");
        var keychain = new KeychainViewModel(new InMemoryLocalStore(), new FakeVault(), "ws-local");
        return new BrowserViewModel(hosts, keychain, logs, new FakeSource("1.0.0"), store);
    }

    [Fact]
    public void UnreadWhenNeverSeen_ClearsAfterSeen()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json"));
        var vm = Build(store);
        Assert.True(vm.HasUnreadChangelog);

        store.Save(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        vm.RefreshChangelogBadge();
        Assert.False(vm.HasUnreadChangelog);
    }
}
