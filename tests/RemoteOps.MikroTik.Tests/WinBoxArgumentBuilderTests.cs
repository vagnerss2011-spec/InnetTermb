using RemoteOps.MikroTik;
using RemoteOps.MikroTik.Models;
using Xunit;

namespace RemoteOps.MikroTik.Tests;

public sealed class WinBoxArgumentBuilderTests
{
    private static WinBoxTarget IPv4(string addr, int port = 8291) =>
        new(addr, WinBoxAddressFamily.IPv4, port);

    private static WinBoxTarget IPv6(string addr, int port = 8291) =>
        new(addr, WinBoxAddressFamily.IPv6, port);

    [Fact]
    public void BuildConnectTo_IPv4_DefaultPort_ReturnsAddressOnly()
    {
        var result = WinBoxArgumentBuilder.BuildConnectTo(IPv4("192.168.88.1"));
        Assert.Equal("192.168.88.1", result);
    }

    [Fact]
    public void BuildConnectTo_IPv4_CustomPort_ReturnsAddressColon()
    {
        var result = WinBoxArgumentBuilder.BuildConnectTo(IPv4("192.168.88.1", 9291));
        Assert.Equal("192.168.88.1:9291", result);
    }

    [Fact]
    public void BuildConnectTo_IPv6Global_ReturnsBracketed()
    {
        var result = WinBoxArgumentBuilder.BuildConnectTo(IPv6("2001:db8::1"));
        Assert.Equal("[2001:db8::1]:8291", result);
    }

    [Fact]
    public void BuildConnectTo_IPv6LinkLocal_ReturnsBracketedWithScopeId()
    {
        var result = WinBoxArgumentBuilder.BuildConnectTo(IPv6("fe80::abcd", 8291));
        Assert.Equal("[fe80::abcd]:8291", result);
    }

    [Fact]
    public void BuildConnectTo_IPv6_CustomPort_ReturnsBracketedWithPort()
    {
        var result = WinBoxArgumentBuilder.BuildConnectTo(IPv6("2001:db8::10", 8292));
        Assert.Equal("[2001:db8::10]:8292", result);
    }

    [Fact]
    public void PopulateArgumentList_NoPassword_PassesEmptyString()
    {
        var request = BuildRequest(includePassword: false);
        var args = new List<string>();

        WinBoxArgumentBuilder.PopulateArgumentList(args, request, allowPassword: false, password: null);

        Assert.Equal("192.168.88.1", args[0]);
        Assert.Equal("admin", args[1]);
        Assert.Equal(string.Empty, args[2]);
    }

    [Fact]
    public void PopulateArgumentList_WithPasswordAllowed_IncludesPassword()
    {
        var request = BuildRequest(includePassword: true);
        var args = new List<string>();

        WinBoxArgumentBuilder.PopulateArgumentList(args, request, allowPassword: true, password: "s3cr3t");

        Assert.Equal("admin", args[1]);
        Assert.Equal("s3cr3t", args[2]);
    }

    [Fact]
    public void PopulateArgumentList_WithPasswordNotAllowed_PasswordNeverAppears()
    {
        var request = BuildRequest(includePassword: true);
        var args = new List<string>();

        WinBoxArgumentBuilder.PopulateArgumentList(args, request, allowPassword: false, password: "s3cr3t");

        Assert.DoesNotContain("s3cr3t", args);
        Assert.Equal(string.Empty, args[2]);
    }

    [Fact]
    public void PopulateArgumentList_WithWorkspace_AppendsAtEnd()
    {
        var request = BuildRequest(workspace: "production");
        var args = new List<string>();

        WinBoxArgumentBuilder.PopulateArgumentList(args, request, allowPassword: false, password: null);

        Assert.Equal("production", args[^1]);
    }

    [Fact]
    public void PopulateArgumentList_RoMon_PrependsFlagAndAgent()
    {
        var request = BuildRequest() with
        {
            RoMon = new RoMonOptions(true, "00:11:22:33:44:55", "10.0.0.1")
        };
        var args = new List<string>();

        WinBoxArgumentBuilder.PopulateArgumentList(args, request, allowPassword: false, password: null);

        Assert.Equal("--romon", args[0]);
        Assert.Equal("00:11:22:33:44:55", args[1]);
        Assert.Equal("10.0.0.1", args[2]);
        Assert.Equal("admin", args[3]);
    }

    [Fact]
    public void PopulateArgumentList_PasswordWithSpace_IsPassedSafely()
    {
        var request = BuildRequest(includePassword: true);
        var args = new List<string>();

        WinBoxArgumentBuilder.PopulateArgumentList(args, request, allowPassword: true, password: "pass with spaces");

        Assert.Equal("pass with spaces", args[2]);
    }

    private static WinBoxLaunchRequest BuildRequest(
        bool includePassword = false,
        string? workspace = null) =>
        new(
            Id: "req-1",
            WorkspaceId: "ws-1",
            HostId: "host-1",
            Target: new WinBoxTarget("192.168.88.1", WinBoxAddressFamily.IPv4, 8291),
            Login: "admin",
            CredentialRefId: null,
            IncludePasswordArgument: includePassword,
            WorkspaceName: workspace,
            RoMon: null,
            RequestedBy: "user@example.com",
            RequestedAt: DateTimeOffset.UtcNow
        );
}
