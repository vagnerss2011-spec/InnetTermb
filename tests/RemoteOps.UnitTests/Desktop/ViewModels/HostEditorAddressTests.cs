using System.Linq;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorAddressTests
{
    private static HostEditorViewModel Vm() =>
        new(new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);

    private void Add(HostEditorViewModel vm, string address)
    {
        vm.NewEndpointAddress = address;
        vm.AddEndpointCommand.Execute(null);
    }

    [Fact]
    public void Ipv6_WithBrackets_IsNormalizedToRawIpv6()
    {
        var vm = Vm();
        Add(vm, "[2001:db8::3001]");
        var ep = vm.Endpoints.Single();
        Assert.Equal("2001:db8::3001", ep.Ipv6);
        Assert.Null(ep.Fqdn);
        Assert.Null(ep.Ipv4);
    }

    [Fact]
    public void Ipv6_WithoutBrackets_GoesToIpv6()
    {
        var vm = Vm();
        Add(vm, "2001:db8::3001");
        var ep = vm.Endpoints.Single();
        Assert.Equal("2001:db8::3001", ep.Ipv6);
        Assert.Null(ep.Fqdn);
    }

    [Fact]
    public void Fqdn_GoesToFqdn()
    {
        var vm = Vm();
        Add(vm, "sn.mynetname22342342.net");
        var ep = vm.Endpoints.Single();
        Assert.Equal("sn.mynetname22342342.net", ep.Fqdn);
        Assert.Null(ep.Ipv4);
        Assert.Null(ep.Ipv6);
    }

    [Fact]
    public void Ipv4_WithSurroundingSpaces_IsTrimmed()
    {
        var vm = Vm();
        Add(vm, "  10.20.30.40  ");
        var ep = vm.Endpoints.Single();
        Assert.Equal("10.20.30.40", ep.Ipv4);
    }
}
