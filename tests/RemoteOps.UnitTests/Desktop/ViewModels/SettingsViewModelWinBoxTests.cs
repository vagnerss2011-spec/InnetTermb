using System.IO;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelWinBoxTests
{
    [Fact]
    public void Save_PersistsWinBoxPathAndHash()
    {
        string p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(p);
        var vm = new SettingsViewModel(store);
        vm.SetWinBox(@"C:\wb\winbox64.exe", "deadbeef");
        vm.SaveCommand.Execute(null);
        AppSettings loaded = store.Load();
        Assert.Equal(@"C:\wb\winbox64.exe", loaded.WinBoxExePath);
        Assert.Equal("deadbeef", loaded.WinBoxSha256);
    }

    [Fact]
    public void Ctor_LoadsExistingWinBoxSettings()
    {
        string p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(p);
        store.Save(new AppSettings { WinBoxExePath = @"C:\pre\winbox64.exe", WinBoxSha256 = "cafe" });
        var vm = new SettingsViewModel(store);
        Assert.Equal(@"C:\pre\winbox64.exe", vm.WinBoxExePath);
        Assert.Equal("cafe", vm.WinBoxSha256);
    }
}
