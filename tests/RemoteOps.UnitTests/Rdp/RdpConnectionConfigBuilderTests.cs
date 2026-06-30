using RemoteOps.Contracts.Assets;
using RemoteOps.Rdp;
using Xunit;

namespace RemoteOps.UnitTests.Rdp;

public sealed class RdpConnectionConfigBuilderTests
{
    private static Endpoint MakeEndpoint(
        string? ipv4 = "10.0.0.5",
        string? ipv6 = null,
        string? fqdn = null,
        int port = 0) => new()
    {
        Id = "ep-1",
        AssetId = "asset-1",
        Protocol = "rdp",
        Ipv4 = ipv4,
        Ipv6 = ipv6,
        Fqdn = fqdn,
        Port = port,
    };

    [Fact]
    public void ResolveHost_PreferIpv6True_AndIpv6Present_ReturnsIpv6()
    {
        var ep = MakeEndpoint(ipv4: "10.0.0.5", ipv6: "fe80::1");
        Assert.Equal("fe80::1", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: true));
    }

    [Fact]
    public void ResolveHost_PreferIpv6True_ButNoIpv6_FallsBackToIpv4()
    {
        var ep = MakeEndpoint(ipv4: "10.0.0.5", ipv6: null);
        Assert.Equal("10.0.0.5", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: true));
    }

    [Fact]
    public void ResolveHost_PreferIpv6False_ReturnsIpv4()
    {
        var ep = MakeEndpoint(ipv4: "10.0.0.5", ipv6: "fe80::1");
        Assert.Equal("10.0.0.5", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: false));
    }

    [Fact]
    public void ResolveHost_NoIps_FallsBackToFqdn()
    {
        var ep = MakeEndpoint(ipv4: null, ipv6: null, fqdn: "host.example.com");
        Assert.Equal("host.example.com", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: false));
    }

    [Fact]
    public void ResolveHost_NoAddressAtAll_Throws()
    {
        var ep = MakeEndpoint(ipv4: null, ipv6: null, fqdn: null);
        Assert.Throws<InvalidOperationException>(() => RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: false));
    }

    [Fact]
    public void Build_PortZero_DefaultsTo3389()
    {
        var ep = MakeEndpoint(port: 0);
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.Equal(3389, config.Port);
    }

    [Fact]
    public void Build_CustomPort_IsPreserved()
    {
        var ep = MakeEndpoint(port: 33890);
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.Equal(33890, config.Port);
    }

    [Fact]
    public void Build_UsernameFromCredentialRef_IsPropagated()
    {
        var ep = MakeEndpoint();
        var config = RdpConnectionConfigBuilder.Build(ep, username: "CORP\\admin", preferIpv6: false);
        Assert.Equal("CORP\\admin", config.Username);
    }

    [Fact]
    public void Build_NlaRequired_DefaultsTrue()
    {
        var ep = MakeEndpoint();
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.True(config.NlaRequired);
    }

    [Fact]
    public void Build_RedirectionPolicy_DefaultsAllOff()
    {
        var ep = MakeEndpoint();
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.False(config.Redirection.ClipboardRedirectionEnabled);
        Assert.False(config.Redirection.DriveRedirectionEnabled);
        Assert.False(config.Redirection.PrinterRedirectionEnabled);
        Assert.False(config.Redirection.AudioRedirectionEnabled);
        Assert.False(config.Redirection.UsbRedirectionEnabled);
    }

    [Fact]
    public void Build_ExplicitRedirectionPolicy_IsHonored()
    {
        var ep = MakeEndpoint();
        var policy = new RdpRedirectionPolicy { ClipboardRedirectionEnabled = true };
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false, redirectionPolicy: policy);
        Assert.True(config.Redirection.ClipboardRedirectionEnabled);
        Assert.False(config.Redirection.DriveRedirectionEnabled);
    }
}
