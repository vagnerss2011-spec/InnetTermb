using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class AppSettingsWinBoxTests
{
    [Fact]
    public void WinBoxFields_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { WinBoxExePath = @"C:\wb\winbox64.exe", WinBoxSha256 = "abcd" });
        AppSettings loaded = store.Load();
        Assert.Equal(@"C:\wb\winbox64.exe", loaded.WinBoxExePath);
        Assert.Equal("abcd", loaded.WinBoxSha256);
    }
}
