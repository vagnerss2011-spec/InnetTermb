using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Domain;

public class DeviceClassifierTests
{
    [Theory]
    [InlineData("MikroTik", "CCR2004", "ssh", DeviceRoles.Router, "mikrotik", "ROS")]
    [InlineData(null, null, "mikrotik", DeviceRoles.Router, "mikrotik", "ROS")]
    [InlineData("MikroTik", "CRS328", "ssh", DeviceRoles.Switch, "mikrotik", "ROS")]
    [InlineData("Huawei", "NE8000", "ssh", DeviceRoles.Router, "huawei", "VRP8")]
    [InlineData("Huawei", "S5720", "telnet", DeviceRoles.Switch, "huawei", "VRP5")]
    [InlineData("Huawei", "MA5800", "telnet", DeviceRoles.Olt, "huawei", "OLT")]
    [InlineData("Debian", null, "ssh", DeviceRoles.ServerLinux, "debian", "DEB")]
    [InlineData("Ubuntu", "22.04", "ssh", DeviceRoles.ServerLinux, "ubuntu", "UBU")]
    [InlineData("A10 Networks", "Thunder", "ssh", DeviceRoles.Other, "a10", "A10")]
    [InlineData("Cisco", "Catalyst 9300", "ssh", DeviceRoles.Switch, "cisco", "CSC")]
    [InlineData(null, null, "rdp", DeviceRoles.ServerWindows, "windows", "WIN")]
    public void Suggest_MapsKnownDevices(string? v, string? m, string? p, string role, string vk, string badge)
    {
        var c = DeviceClassifier.Suggest(v, m, p);
        Assert.Equal(role, c.Role);
        Assert.Equal(vk, c.VendorKey);
        Assert.Equal(badge, c.BadgeLabel);
    }

    [Fact]
    public void Suggest_Unknown_ReturnsOther_ZeroConfidence()
    {
        var c = DeviceClassifier.Suggest("AcmeCorp", "X1", "ssh");
        Assert.Equal(DeviceRoles.Other, c.Role);
        Assert.Equal(0, c.Confidence);
        Assert.Equal("acmecorp", c.VendorKey);
    }

    [Fact]
    public void Suggest_Empty_DoesNotThrow_AndIsOther()
    {
        var c = DeviceClassifier.Suggest(null, null, null);
        Assert.Equal(DeviceRoles.Other, c.Role);
        Assert.Null(c.VendorKey);
    }
}
