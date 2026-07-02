using System.Collections.Generic;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorProtocolSyncTests
{
    private static Asset AssetWith(string protocol, int port) => new()
    {
        Id = "a1",
        WorkspaceId = "ws-local",
        Name = "r1",
        Endpoints = new List<Endpoint>
        {
            new() { Id = "e1", AssetId = "a1", Protocol = protocol, Ipv4 = "10.0.0.1", Port = port },
        },
    };

    [Fact]
    public void Edit_MikroTikHost_SyncsProtocolAndDefaultPort()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws-local", AssetWith("mikrotik", 8291), groupId: null);
        Assert.Equal("mikrotik", vm.NewEndpointProtocol);
        Assert.Equal(8291, vm.NewEndpointPort);
    }

    [Fact]
    public void Edit_TelnetHost_SyncsProtocol()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws-local", AssetWith("telnet", 23), groupId: null);
        Assert.Equal("telnet", vm.NewEndpointProtocol);
    }

    [Fact]
    public void New_DefaultsToSsh()
    {
        var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);
        Assert.Equal("ssh", vm.NewEndpointProtocol);
        Assert.Equal(22, vm.NewEndpointPort);
    }
}
