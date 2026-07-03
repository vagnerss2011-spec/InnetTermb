using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class PrivateKeyInputTests
{
    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----", PrivateKeyKind.Valid)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----", PrivateKeyKind.Valid)]
    [InlineData("   -----BEGIN PRIVATE KEY-----\nx", PrivateKeyKind.Valid)]
    [InlineData("PuTTY-User-Key-File-2: ssh-rsa\nEncryption: none", PrivateKeyKind.PuttyPpk)]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519", PrivateKeyKind.PuttyPpk)]
    [InlineData("qualquer lixo", PrivateKeyKind.Invalid)]
    [InlineData("", PrivateKeyKind.Invalid)]
    public void Classify_Works(string text, PrivateKeyKind expected)
        => Assert.Equal(expected, PrivateKeyInput.Classify(text));
}
