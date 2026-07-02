using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Reporting;

public sealed class DiagnosticsProviderTests
{
    [Fact]
    public void BuildDiagnostics_IncludesVersionOsDeviceAndRecentLogs()
    {
        var logs = new LogsViewModel();
        logs.Emit("evento A");
        logs.Emit("evento B");
        var provider = new DiagnosticsProvider(logs, "1.2.3", "Windows 11", "dev-42");

        string text = provider.BuildDiagnostics();

        Assert.Contains("1.2.3", text);
        Assert.Contains("Windows 11", text);
        Assert.Contains("dev-42", text);
        Assert.Contains("evento A", text);
        Assert.Contains("evento B", text);
    }

    [Fact]
    public void BuildDiagnostics_NoDeviceId_OmitsDeviceLine()
    {
        var provider = new DiagnosticsProvider(new LogsViewModel(), "1.0.0", "Windows", deviceId: null);
        Assert.DoesNotContain("Device:", provider.BuildDiagnostics());
    }
}
