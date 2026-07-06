using RemoteOps.Terminal.Ssh;
using Renci.SshNet.Common;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshConnectionErrorTests
{
    [Fact]
    public void Auth_MapsToPortugueseUserPassword()
    {
        string msg = SshConnectionError.Describe(new SshAuthenticationException("Permission denied"), "10.0.0.1", 22);
        Assert.Contains("usuário ou senha incorretos", msg);
        Assert.Contains("10.0.0.1", msg);
    }

    [Fact]
    public void Timeout_MapsToPortuguese()
    {
        string msg = SshConnectionError.Describe(new SshOperationTimeoutException("timed out"), "sw1.net", 22);
        Assert.Contains("Tempo esgotado", msg);
        Assert.Contains("sw1.net:22", msg);
    }

    [Fact]
    public void ConnectionException_HintsAlgorithmProfile()
    {
        string msg = SshConnectionError.Describe(new SshConnectionException("kex failed"), "olt.local", 22);
        Assert.Contains("algoritmos legados", msg);
    }

    [Fact]
    public void AuthWrappedAsInner_IsStillMapped()
    {
        var wrapped = new System.InvalidOperationException("boom", new SshAuthenticationException("denied"));
        string msg = SshConnectionError.Describe(wrapped, "h", 22);
        Assert.Contains("usuário ou senha incorretos", msg);
    }

    [Fact]
    public void Unknown_FallsBackToRawMessage()
    {
        string msg = SshConnectionError.Describe(new System.Exception("weird"), "h", 22);
        Assert.Contains("weird", msg);
        Assert.Contains("h:22", msg);
    }
}
