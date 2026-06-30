using RemoteOps.MikroTik;
using RemoteOps.MikroTik.Models;
using Xunit;

namespace RemoteOps.MikroTik.Tests;

public sealed class WinBoxPolicyTests
{
    private static WinBoxLaunchRequest Request(bool includePassword) =>
        new("req-1", "ws-1", "host-1",
            new WinBoxTarget("10.0.0.1", WinBoxAddressFamily.IPv4, 8291),
            "admin", null, includePassword, null, null,
            "user@example.com", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Policy_GlobalFalse_NeverAllowsPasswordArg()
    {
        var policy = new LocalWinBoxPolicyProvider(globalAllowPasswordArgument: false);
        var decision = await policy.EvaluateAsync(Request(includePassword: true));

        Assert.True(decision.Allowed);
        Assert.False(decision.AllowPasswordArgument);
    }

    [Fact]
    public async Task Policy_GlobalTrue_RequestFalse_DeniesPasswordArg()
    {
        var policy = new LocalWinBoxPolicyProvider(globalAllowPasswordArgument: true);
        var decision = await policy.EvaluateAsync(Request(includePassword: false));

        Assert.True(decision.Allowed);
        Assert.False(decision.AllowPasswordArgument);
    }

    [Fact]
    public async Task Policy_BothTrue_AllowsPasswordArg()
    {
        var policy = new LocalWinBoxPolicyProvider(globalAllowPasswordArgument: true);
        var decision = await policy.EvaluateAsync(Request(includePassword: true));

        Assert.True(decision.Allowed);
        Assert.True(decision.AllowPasswordArgument);
    }
}
