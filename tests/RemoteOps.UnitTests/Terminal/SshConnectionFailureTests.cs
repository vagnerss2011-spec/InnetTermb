using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal;
using RemoteOps.Terminal.Ssh;
using RemoteOps.UnitTests.Terminal.Fakes;
using Renci.SshNet.Common;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshConnectionFailureTests
{
    private static async Task<SshSessionProvider> BuildAsync(FakeSshConnectionFactory factory)
    {
        var vault = new FakeVault();
        var eps = new InMemoryEndpointResolver();
        var crs = new InMemoryCredentialRefResolver();
        string env = await vault.SetupAsync("s3nha-teste", "c1");
        eps.Add(new Endpoint { Id = "e1", AssetId = "a1", Protocol = RemoteProtocol.Ssh, Ipv4 = "10.0.0.1", Port = 22 });
        crs.Add(new CredentialRef
        {
            Id = "c1",
            Name = "p",
            Type = CredentialTypes.Password,
            SecretEnvelopeId = env,
            Metadata = new CredentialMetadata { Username = "root" },
        });
        return new SshSessionProvider(eps, crs, vault, new FakeTerminalSecurityContext(),
            new FakeHostKeyConfirmation(true), new InMemoryTerminalAuditSink(), factory, new HostKeyStore(path: null));
    }

    private static SessionRequest Request() => new()
    {
        SessionId = "s1",
        Protocol = RemoteProtocol.Ssh,
        EndpointId = "e1",
        CredentialRefId = "c1",
        PreferIpv6 = false,
    };

    [Fact]
    public async Task OpenAsync_WrongPassword_ThrowsPortugueseMessage()
    {
        var factory = new FakeSshConnectionFactory
        {
            ForceValidatorResult = true, // host key aceita → sem prompt TOFU
            SimulatedConnectException = new SshAuthenticationException("Permission denied (password)."),
        };
        var provider = await BuildAsync(factory);

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => provider.OpenAsync(Request(), CancellationToken.None));

        Assert.Contains("usuário ou senha incorretos", ex.Message);
        Assert.DoesNotContain("s3nha-teste", ex.Message); // segredo nunca na mensagem
    }

    [Fact]
    public async Task OpenAsync_Timeout_ThrowsPortugueseMessage()
    {
        var factory = new FakeSshConnectionFactory
        {
            ForceValidatorResult = true,
            SimulatedConnectException = new SshOperationTimeoutException("timed out"),
        };
        var provider = await BuildAsync(factory);

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => provider.OpenAsync(Request(), CancellationToken.None));

        Assert.Contains("Tempo esgotado", ex.Message);
    }
}
