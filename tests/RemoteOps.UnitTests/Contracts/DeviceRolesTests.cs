using System.Linq;
using RemoteOps.Contracts.Assets;
using Xunit;

namespace RemoteOps.UnitTests.Contracts;

public class DeviceRolesTests
{
    [Fact]
    public void All_ContainsEveryRole_AndNoDuplicates()
    {
        Assert.Equal(9, DeviceRoles.All.Count);
        Assert.Equal(DeviceRoles.All.Count, DeviceRoles.All.Distinct().Count());
        Assert.Contains(DeviceRoles.Router, DeviceRoles.All);
        Assert.Contains(DeviceRoles.Switch, DeviceRoles.All);
        Assert.Contains(DeviceRoles.ServerLinux, DeviceRoles.All);
        Assert.Contains(DeviceRoles.Olt, DeviceRoles.All);
        Assert.Contains(DeviceRoles.Other, DeviceRoles.All);
    }

    [Fact]
    public void Asset_CarriesDeviceRole()
    {
        var asset = new Asset { Id = "a1", WorkspaceId = "ws", Name = "r1", DeviceRole = DeviceRoles.Router };
        Assert.Equal("router", asset.DeviceRole);
    }
}
