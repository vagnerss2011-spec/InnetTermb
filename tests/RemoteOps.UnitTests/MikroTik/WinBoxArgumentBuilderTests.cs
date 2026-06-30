using RemoteOps.Contracts.ExternalTools;
using RemoteOps.MikroTik;

using Xunit;

namespace RemoteOps.UnitTests.MikroTik;

public sealed class WinBoxArgumentBuilderTests
{
    // ── BuildConnectTo ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildConnectTo_Ipv4_NoPort_ReturnsAddress()
    {
        var target = new ExternalToolTarget { Address = "192.0.2.1", Port = 0 };
        Assert.Equal("192.0.2.1", WinBoxArgumentBuilder.BuildConnectTo(target));
    }

    [Fact]
    public void BuildConnectTo_Ipv4_WithPort_AppendsPort()
    {
        var target = new ExternalToolTarget { Address = "192.0.2.1", Port = 8292 };
        Assert.Equal("192.0.2.1:8292", WinBoxArgumentBuilder.BuildConnectTo(target));
    }

    [Fact]
    public void BuildConnectTo_Ipv6Global_WithPort_UsesBrackets()
    {
        var target = new ExternalToolTarget { Address = "2001:db8::10", Port = 8292 };
        Assert.Equal("[2001:db8::10]:8292", WinBoxArgumentBuilder.BuildConnectTo(target));
    }

    [Fact]
    public void BuildConnectTo_Ipv6Global_NoPort_BracketsOnly()
    {
        var target = new ExternalToolTarget { Address = "2001:db8::10", Port = 0 };
        Assert.Equal("[2001:db8::10]", WinBoxArgumentBuilder.BuildConnectTo(target));
    }

    [Fact]
    public void BuildConnectTo_Ipv6LinkLocal_WithPort_UsesBracketsWithScope()
    {
        var target = new ExternalToolTarget { Address = "fe80::abcd%12", Port = 8291 };
        Assert.Equal("[fe80::abcd%12]:8291", WinBoxArgumentBuilder.BuildConnectTo(target));
    }

    [Fact]
    public void BuildConnectTo_AlreadyBracketed_StripsThenRebrackets()
    {
        var target = new ExternalToolTarget { Address = "[2001:db8::10]", Port = 8292 };
        Assert.Equal("[2001:db8::10]:8292", WinBoxArgumentBuilder.BuildConnectTo(target));
    }

    // ── Build — argumento único (sem login/senha) ───────────────────────────────

    [Fact]
    public void Build_ModoA_NoLogin_NoPassword_OnlyConnectTo()
    {
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: null, includePassword: false);
        var args = WinBoxArgumentBuilder.Build(request, password: null, passwordArgumentAllowed: false);

        Assert.Single(args);
        Assert.Equal("10.0.0.1", args[0]);
        Assert.DoesNotContain(string.Empty, args);
    }

    [Fact]
    public void Build_ModoA_WithLogin_NoPassword_TwoArgs()
    {
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: "admin", includePassword: false);
        var args = WinBoxArgumentBuilder.Build(request, password: null, passwordArgumentAllowed: false);

        Assert.Equal(2, args.Count);
        Assert.Equal("10.0.0.1", args[0]);
        Assert.Equal("admin", args[1]);
        Assert.DoesNotContain(string.Empty, args);
    }

    [Fact]
    public void Build_ModoB_WithLogin_WithPassword_ThreeArgs()
    {
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: "admin", includePassword: true);
        var args = WinBoxArgumentBuilder.Build(request, password: "S3cret", passwordArgumentAllowed: true);

        Assert.Equal(3, args.Count);
        Assert.Equal("10.0.0.1", args[0]);
        Assert.Equal("admin", args[1]);
        Assert.Equal("S3cret", args[2]);
    }

    [Fact]
    public void Build_EmptyPassword_NoPasswordArg_NoEmptyArgv()
    {
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: "admin", includePassword: true);
        var args = WinBoxArgumentBuilder.Build(request, password: "", passwordArgumentAllowed: true);

        Assert.Equal(2, args.Count); // connect-to + login only
        Assert.DoesNotContain(string.Empty, args);
    }

    [Fact]
    public void Build_WhitespacePassword_PassedAsIs()
    {
        // IsNullOrEmpty("   ") é false → senha espaços é incluída; ArgumentList faz o quoting.
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: "admin", includePassword: true);
        var args = WinBoxArgumentBuilder.Build(request, password: "   ", passwordArgumentAllowed: true);

        Assert.Equal(3, args.Count);
        Assert.Equal("   ", args[2]);
    }

    [Fact]
    public void Build_PasswordWithSpaces_ArgPreserved()
    {
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: "admin", includePassword: true);
        var args = WinBoxArgumentBuilder.Build(request, password: "pass word!", passwordArgumentAllowed: true);

        Assert.Equal(3, args.Count);
        Assert.Equal("pass word!", args[2]); // ArgumentList fará o quoting correto
    }

    [Fact]
    public void Build_PolicyDeniesPassword_IgnoresIncludePassword()
    {
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: "admin", includePassword: true);
        var args = WinBoxArgumentBuilder.Build(request, password: "S3cret", passwordArgumentAllowed: false);

        // passwordArgumentAllowed=false → sem senha no argv
        Assert.Equal(2, args.Count);
        Assert.DoesNotContain("S3cret", args);
    }

    [Fact]
    public void Build_NoLogin_PasswordNotAdded_EvenIfAllowed()
    {
        // Sem login, senha não pode ser adicionada (deslocaria a posição)
        var request = MakeRequest(address: "10.0.0.1", port: 0, login: null, includePassword: true);
        var args = WinBoxArgumentBuilder.Build(request, password: "S3cret", passwordArgumentAllowed: true);

        Assert.Single(args);
        Assert.DoesNotContain(string.Empty, args);
    }

    [Fact]
    public void Build_NeverProducesEmptyArgv()
    {
        // Modo A, sem login nem senha
        var modes = new[]
        {
            (login: (string?)null, pw: (string?)null, include: false, pwAllowed: false),
            (login: null,           pw: null,           include: true,  pwAllowed: true),
            (login: "",             pw: "secret",        include: true,  pwAllowed: true),
            (login: "admin",        pw: (string?)null,  include: false, pwAllowed: false),
            (login: "admin",        pw: "",             include: true,  pwAllowed: true),
        };

        foreach (var (login, pw, include, pwAllowed) in modes)
        {
            var request = MakeRequest("10.0.0.1", 0, login, include);
            var args = WinBoxArgumentBuilder.Build(request, pw, pwAllowed);
            Assert.DoesNotContain(string.Empty, args);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static ExternalToolLaunchRequest MakeRequest(
        string address,
        int port,
        string? login,
        bool includePassword)
        => new()
        {
            Id = "test-01",
            WorkspaceId = "ws-01",
            Tool = "winbox",
            Target = new ExternalToolTarget { Address = address, Port = port },
            Login = login,
            IncludePasswordArgument = includePassword,
            RequestedBy = "user-01",
            RequestedAt = DateTimeOffset.UtcNow,
        };
}
