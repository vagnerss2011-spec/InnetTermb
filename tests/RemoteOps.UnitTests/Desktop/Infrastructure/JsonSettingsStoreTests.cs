using System.Collections.Generic;
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);

        AppSettings settings = store.Load();

        Assert.Empty(settings.Flags);
        Assert.Equal("slate-signal-dark", settings.Theme);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsFlags()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);

        store.Save(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.RdpEnabled] = true },
        });

        AppSettings loaded = store.Load();
        Assert.True(loaded.Flags[FeatureFlagNames.RdpEnabled]);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not valid json ");
        var store = new JsonSettingsStore(path);

        AppSettings settings = store.Load();
        Assert.Empty(settings.Flags);
    }
}
