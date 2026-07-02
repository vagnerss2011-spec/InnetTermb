using System.Globalization;
using RemoteOps.Desktop.Converters;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Converters;

public sealed class EndpointAddressConverterTests
{
    private readonly EndpointAddressConverter _sut = new();

    [Fact]
    public void Convert_UsesIpv4_WhenPresent()
    {
        var values = new object?[] { "ssh", "10.0.0.1", null, null, 22 };
        var result = _sut.Convert(values!, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("ssh — 10.0.0.1:22", result);
    }

    [Fact]
    public void Convert_FallsBackToIpv6_WhenIpv4Absent()
    {
        var values = new object?[] { "ssh", null, "fe80::1", null, 22 };
        var result = _sut.Convert(values!, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("ssh — fe80::1:22", result);
    }

    [Fact]
    public void Convert_FallsBackToFqdn_WhenIpv4AndIpv6Absent()
    {
        var values = new object?[] { "ssh", null, null, "router.example.com", 22 };
        var result = _sut.Convert(values!, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("ssh — router.example.com:22", result);
    }

    [Fact]
    public void Convert_ReturnsEmptyAddress_WhenAllAbsent()
    {
        var values = new object?[] { "ssh", null, null, null, 22 };
        var result = _sut.Convert(values!, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("ssh — :22", result);
    }
}
