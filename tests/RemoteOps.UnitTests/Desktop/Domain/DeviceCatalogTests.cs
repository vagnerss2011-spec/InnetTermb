using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Domain;

public class DeviceCatalogTests
{
    [Fact]
    public void EveryRole_HasGeometryAndLabel()
    {
        foreach (var r in DeviceRoles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(DeviceCatalog.RoleGlyphGeometry(r)), $"geometry vazia p/ {r}");
            Assert.False(string.IsNullOrWhiteSpace(DeviceCatalog.RoleLabel(r)), $"label vazio p/ {r}");
        }
    }

    [Fact]
    public void NullRole_FallsBackToGeneric()
    {
        Assert.Equal(DeviceCatalog.RoleLabel(DeviceRoles.Other), DeviceCatalog.RoleLabel(null));
        Assert.False(string.IsNullOrWhiteSpace(DeviceCatalog.RoleGlyphGeometry(null)));
    }

    [Theory]
    [InlineData("huawei")]
    [InlineData("mikrotik")]
    [InlineData("debian")]
    [InlineData("a10")]
    public void KnownVendor_HasHexColor(string vendorKey)
        => Assert.Matches("^#[0-9A-Fa-f]{6}$", DeviceCatalog.VendorColorHex(vendorKey));

    [Fact]
    public void UnknownVendor_HasFallbackColor()
        => Assert.Matches("^#[0-9A-Fa-f]{6}$", DeviceCatalog.VendorColorHex("acme"));

    [Fact]
    public void LogoFileName_UsesVendorKey()
        => Assert.Equal("huawei.png", DeviceCatalog.LogoFileName("huawei"));

    [Fact]
    public void LogoFileName_NullOrEmptyVendor_IsNull()
    {
        Assert.Null(DeviceCatalog.LogoFileName(null));
        Assert.Null(DeviceCatalog.LogoFileName("  "));
    }
}
