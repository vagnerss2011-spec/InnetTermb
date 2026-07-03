using System.Text;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal;
using RemoteOps.Terminal.Ssh;
using RemoteOps.UnitTests.Terminal.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshKeyAuthTests
{
    private const string KeyPem = "-----BEGIN OPENSSH PRIVATE KEY-----\nKEYBODY\n-----END OPENSSH PRIVATE KEY-----";

    private static SshSessionProvider BuildProvider(
        FakeSshConnectionFactory factory,
        InMemoryEndpointResolver eps,
        InMemoryCredentialRefResolver crs,
        FakeVault vault)
        => new(eps, crs, vault, new FakeTerminalSecurityContext(), new FakeHostKeyConfirmation(true),
               new InMemoryTerminalAuditSink(), factory);

    private static SessionRequest Request() => new()
    {
        SessionId = "s1",
        Protocol = RemoteProtocol.Ssh,
        EndpointId = "e1",
        CredentialRefId = "c1",
        PreferIpv6 = false,
    };

    [Fact]
    public async Task PrivateKeyCredential_PassesKeyAndProfile_NotPassword()
    {
        var factory = new FakeSshConnectionFactory { ForceValidatorResult = true };
        var vault = new FakeVault();
        var eps = new InMemoryEndpointResolver();
        var crs = new InMemoryCredentialRefResolver();

        string keyEnv = await vault.SetupAsync(KeyPem, "c1-key");
        string ppEnv = await vault.SetupAsync("s3nha", "c1-pp");

        eps.Add(new Endpoint
        {
            Id = "e1",
            AssetId = "a1",
            Protocol = RemoteProtocol.Ssh,
            Ipv4 = "10.0.0.1",
            Port = 22,
            Profile = new EndpointProfile { SshAlgorithmProfile = "strict" },
        });
        crs.Add(new CredentialRef
        {
            Id = "c1",
            Name = "k",
            Type = CredentialTypes.PrivateKey,
            SecretEnvelopeId = keyEnv,
            Metadata = new CredentialMetadata { Username = "root", HasPrivateKey = true, PassphraseEnvelopeId = ppEnv },
        });

        await BuildProvider(factory, eps, crs, vault).OpenAsync(Request(), CancellationToken.None);

        Assert.NotNull(factory.LastPrivateKeySnapshot);
        Assert.Equal(KeyPem, Encoding.UTF8.GetString(factory.LastPrivateKeySnapshot!));
        Assert.Equal("s3nha", factory.LastOptions!.PrivateKeyPassphrase);
        Assert.Null(factory.LastOptions.Password);
        Assert.Equal("strict", factory.LastOptions.AlgorithmProfile);
    }

    [Fact]
    public async Task PasswordCredential_PassesPassword_NotKey()
    {
        var factory = new FakeSshConnectionFactory { ForceValidatorResult = true };
        var vault = new FakeVault();
        var eps = new InMemoryEndpointResolver();
        var crs = new InMemoryCredentialRefResolver();

        string env = await vault.SetupAsync("p4ss", "c1-pwd");
        eps.Add(new Endpoint { Id = "e1", AssetId = "a1", Protocol = RemoteProtocol.Ssh, Ipv4 = "10.0.0.1", Port = 22 });
        crs.Add(new CredentialRef
        {
            Id = "c1",
            Name = "p",
            Type = CredentialTypes.Password,
            SecretEnvelopeId = env,
            Metadata = new CredentialMetadata { Username = "root" },
        });

        await BuildProvider(factory, eps, crs, vault).OpenAsync(Request(), CancellationToken.None);

        Assert.Equal("p4ss", factory.LastOptions!.Password);
        Assert.Null(factory.LastOptions.PrivateKeyUtf8);
    }
}
