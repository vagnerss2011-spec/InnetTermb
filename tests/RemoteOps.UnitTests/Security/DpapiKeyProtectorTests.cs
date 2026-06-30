using System.Security.Cryptography;
using System.Text;

using RemoteOps.Security.Crypto;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// Testes do protetor DPAPI real. Só executam efetivamente no Windows
/// (o job de CI roda em windows-latest); em outras plataformas viram no-op
/// para não quebrar builds locais cross-platform.
/// </summary>
public sealed class DpapiKeyProtectorTests
{
    [Fact]
    public void Protect_Then_Unprotect_RoundTrips_On_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiKeyProtector();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] entropy = Encoding.UTF8.GetBytes("remoteops:wdk:ws-01");

        byte[] blob = protector.Protect(key, entropy);
        byte[] recovered = protector.Unprotect(blob, entropy);

        Assert.Equal(key, recovered);
        // O blob protegido não pode ser o material em claro.
        Assert.NotEqual(key, blob);
    }

    [Fact]
    public void Unprotect_With_Wrong_Entropy_Fails_On_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiKeyProtector();
        byte[] key = RandomNumberGenerator.GetBytes(32);

        byte[] blob = protector.Protect(key, Encoding.UTF8.GetBytes("entropy-A"));

        Assert.Throws<CryptographicException>(
            () => protector.Unprotect(blob, Encoding.UTF8.GetBytes("entropy-B")));
    }
}
