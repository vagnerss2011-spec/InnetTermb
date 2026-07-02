using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class AppSettingsChangelogTests
{
    [Fact]
    public void LastSeenChangelogVersion_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        AppSettings loaded = store.Load();
        Assert.Equal("1.0.0", loaded.LastSeenChangelogVersion);
    }
}
