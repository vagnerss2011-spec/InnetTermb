using System.Text;
using RemoteOps.Terminal.Ssh;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class RenciSshConnectionFactoryTests
{
    [Fact]
    public void Create_WithInvalidPrivateKey_ThrowsReadableError()
    {
        var factory = new RenciSshConnectionFactory();
        var opts = new SshConnectionOptions
        {
            Host = "h",
            Port = 22,
            Username = "u",
            PrivateKeyUtf8 = Encoding.UTF8.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----\nnotarealkey\n-----END OPENSSH PRIVATE KEY-----"), // pragma: allowlist secret (fixture sintético)
        };
        var ex = Assert.ThrowsAny<System.Exception>(() => factory.Create(opts));
        Assert.Contains("chave", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_WithPassword_DoesNotThrow()
    {
        var factory = new RenciSshConnectionFactory();
        using var conn = factory.Create(new SshConnectionOptions { Host = "h", Port = 22, Username = "u", Password = "p" });
        Assert.NotNull(conn);
    }
}
