using System.Text.Json;
using RemoteOps.Contracts.Assets;
using Xunit;

namespace RemoteOps.UnitTests.Contracts;

public sealed class CredentialTypesTests
{
    [Fact]
    public void Values_AreStable()
    {
        Assert.Equal("password", CredentialTypes.Password);
        Assert.Equal("privateKey", CredentialTypes.PrivateKey);
        Assert.Equal("privateKeyPassphrase", CredentialTypes.PrivateKeyPassphrase);
    }

    [Fact]
    public void Metadata_PassphraseEnvelopeId_RoundTripsJson()
    {
        var m = new CredentialMetadata { Username = "root", HasPrivateKey = true, PassphraseEnvelopeId = "env-pp" };
        var back = JsonSerializer.Deserialize<CredentialMetadata>(JsonSerializer.Serialize(m))!;
        Assert.True(back.HasPrivateKey);
        Assert.Equal("env-pp", back.PassphraseEnvelopeId);
    }

    [Fact]
    public void EndpointProfile_SshAlgorithmProfile_RoundTripsJson()
    {
        var p = new EndpointProfile { SshAlgorithmProfile = "strict" };
        var back = JsonSerializer.Deserialize<EndpointProfile>(JsonSerializer.Serialize(p))!;
        Assert.Equal("strict", back.SshAlgorithmProfile);
    }
}
