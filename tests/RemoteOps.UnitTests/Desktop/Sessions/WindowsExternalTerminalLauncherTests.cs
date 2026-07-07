using System.Diagnostics;
using RemoteOps.Desktop.Sessions;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Sessions;

public sealed class WindowsExternalTerminalLauncherTests
{
    [Fact]
    public void BuildSshArguments_WithUsername_UsesUserAtHostAndPort()
    {
        var args = WindowsExternalTerminalLauncher.BuildSshArguments(new SshLaunchTarget("10.0.0.1", 2222, "admin"));
        Assert.Equal("-p 2222 admin@10.0.0.1", args);
    }

    [Fact]
    public void BuildSshArguments_WithoutUsername_UsesHostOnly()
    {
        var args = WindowsExternalTerminalLauncher.BuildSshArguments(new SshLaunchTarget("host.example", 22, null));
        Assert.Equal("-p 22 host.example", args);
    }

    [Fact]
    public void LaunchSshAsync_StartsSshExe_ViaShellExecute()
    {
        ProcessStartInfo? captured = null;
        var launcher = new WindowsExternalTerminalLauncher(psi => { captured = psi; return null; });

        launcher.LaunchSshAsync(new SshLaunchTarget("10.0.0.1", 22, "admin"));

        Assert.NotNull(captured);
        Assert.Equal("ssh.exe", captured!.FileName);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("-p 22 admin@10.0.0.1", captured.Arguments);
    }
}
