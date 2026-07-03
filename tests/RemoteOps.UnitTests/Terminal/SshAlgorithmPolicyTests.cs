using RemoteOps.Terminal.Ssh;
using Renci.SshNet;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshAlgorithmPolicyTests
{
    private static ConnectionInfo NewInfo()
        => new("h", 22, "u", new PasswordAuthenticationMethod("u", "p"));

    [Fact]
    public void Auto_DoesNotChangeAlgorithms()
    {
        var info = NewInfo();
        int kex = info.KeyExchangeAlgorithms.Count;
        int enc = info.Encryptions.Count;
        SshAlgorithmPolicy.Apply(info, SshAlgorithmPolicy.Auto);
        Assert.Equal(kex, info.KeyExchangeAlgorithms.Count);
        Assert.Equal(enc, info.Encryptions.Count);
    }

    [Fact]
    public void Strict_RemovesWeakAlgorithms()
    {
        var info = NewInfo();
        SshAlgorithmPolicy.Apply(info, SshAlgorithmPolicy.Strict);
        Assert.DoesNotContain("diffie-hellman-group1-sha1", info.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("diffie-hellman-group14-sha1", info.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("ssh-rsa", info.HostKeyAlgorithms.Keys);
        Assert.DoesNotContain("aes256-cbc", info.Encryptions.Keys);
        Assert.DoesNotContain("3des-cbc", info.Encryptions.Keys);
        Assert.DoesNotContain("hmac-sha1", info.HmacAlgorithms.Keys);
    }

    [Fact]
    public void Strict_KeepsStrongAlgorithms()
    {
        var info = NewInfo();
        SshAlgorithmPolicy.Apply(info, SshAlgorithmPolicy.Strict);
        Assert.Contains("curve25519-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("aes256-ctr", info.Encryptions.Keys);
        Assert.Contains("ssh-ed25519", info.HostKeyAlgorithms.Keys);
        Assert.Contains("hmac-sha2-256", info.HmacAlgorithms.Keys);
    }

    [Fact]
    public void NullProfile_DoesNotThrow() => SshAlgorithmPolicy.Apply(NewInfo(), null);
}
