using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class LogsViewModelTests
{
    [Fact]
    public void Emit_AppendsEvent()
    {
        var vm = new LogsViewModel();
        vm.Emit("sessão aberta: r1 (ssh)");
        Assert.Contains("sessão aberta: r1 (ssh)", vm.Events);
    }
}
