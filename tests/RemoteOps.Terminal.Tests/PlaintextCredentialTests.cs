using FluentAssertions;
using RemoteOps.Contracts.Models;

namespace RemoteOps.Terminal.Tests;

public sealed class PlaintextCredentialTests
{
    [Fact]
    public void Dispose_ZerosPasswordBytes()
    {
        var raw = new byte[] { 115, 101, 99, 114, 101, 116 }; // "secret"
        var cred = PlaintextCredential.WithPassword("user", raw);

        cred.Password.Should().NotBeNull();
        cred.Dispose();

        // After dispose the backing array must be zeroed.
        raw.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void Dispose_ZerosPrivateKeyBytes()
    {
        var pem = System.Text.Encoding.UTF8.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----");
        var cred = PlaintextCredential.WithPrivateKey("user", pem);

        cred.PrivateKey.Should().NotBeNull();
        cred.Dispose();

        pem.Should().AllBeEquivalentTo((byte)0);
    }

    [Fact]
    public void DoubleDispose_IsIdempotent()
    {
        var cred = PlaintextCredential.WithPassword("user", new byte[] { 1, 2, 3 });
        var act = () => { cred.Dispose(); cred.Dispose(); };
        act.Should().NotThrow();
    }

    [Fact]
    public void Password_AsString_ReturnsUtf8()
    {
        var cred = PlaintextCredential.WithPassword("user", System.Text.Encoding.UTF8.GetBytes("p@ss!"));
        cred.Password!.Value.AsString().Should().Be("p@ss!");
        cred.Dispose();
    }
}
