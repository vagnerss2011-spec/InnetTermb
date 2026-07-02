using RemoteOps.Desktop.Integration;
using RemoteOps.MikroTik;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

public sealed class WinBoxManifestResolverTests
{
    [Fact]
    public void Settings_TakePrecedence_OverEnvAndDefault()
    {
        WinBoxToolManifest m = WinBoxManifestResolver.Resolve(@"C:\s\wb.exe", "aaa", @"C:\e\wb.exe", "bbb");
        Assert.Equal(@"C:\s\wb.exe", m.ExecutablePath);
        Assert.Equal("aaa", m.Sha256);
    }

    [Fact]
    public void Env_UsedWhenSettingsEmpty_ElseDefaultPath()
    {
        WinBoxToolManifest e = WinBoxManifestResolver.Resolve(null, null, @"C:\e\wb.exe", "bbb");
        Assert.Equal(@"C:\e\wb.exe", e.ExecutablePath);
        Assert.Equal("bbb", e.Sha256);

        WinBoxToolManifest d = WinBoxManifestResolver.Resolve(null, null, null, null);
        Assert.Equal(@"C:\Tools\WinBox\winbox64.exe", d.ExecutablePath);
        Assert.Null(d.Sha256);
    }

    [Fact]
    public void BlankStrings_TreatedAsUnset()
    {
        WinBoxToolManifest m = WinBoxManifestResolver.Resolve("   ", "  ", @"C:\e\wb.exe", "bbb");
        Assert.Equal(@"C:\e\wb.exe", m.ExecutablePath);
        Assert.Equal("bbb", m.Sha256);
    }
}
