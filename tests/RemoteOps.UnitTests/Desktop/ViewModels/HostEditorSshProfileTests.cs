using System.Linq;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorSshProfileTests
{
    private static HostEditorViewModel Vm() => new(new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);

    [Fact]
    public void AddSshEndpoint_WithStrict_SetsProfile()
    {
        var vm = Vm();
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointSshProfile = "strict";
        vm.AddEndpointCommand.Execute(null);
        Assert.Equal("strict", vm.Endpoints.Single().Profile!.SshAlgorithmProfile);
    }

    [Fact]
    public void AddSshEndpoint_Auto_LeavesProfileNull()
    {
        var vm = Vm();
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointSshProfile = "auto";
        vm.AddEndpointCommand.Execute(null);
        Assert.Null(vm.Endpoints.Single().Profile);
    }

    [Fact]
    public void AddEndpoint_ResetsProfileToAuto()
    {
        var vm = Vm();
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointSshProfile = "strict";
        vm.AddEndpointCommand.Execute(null);
        Assert.Equal("auto", vm.NewEndpointSshProfile);
    }
}
