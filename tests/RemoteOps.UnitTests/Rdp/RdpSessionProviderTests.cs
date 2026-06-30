using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Rdp;
using RemoteOps.UnitTests.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Rdp;

public sealed class RdpSessionProviderTests
{
    private const string EndpointId = "ep-1";
    private const string CredRefId = "cr-1";

    private static (RdpSessionProvider provider, InMemoryRdpEndpointResolver eps,
        InMemoryRdpCredentialRefResolver creds, InMemoryRdpAuditSink audit) Build()
    {
        var eps = new InMemoryRdpEndpointResolver();
        var creds = new InMemoryRdpCredentialRefResolver();
        var audit = new InMemoryRdpAuditSink();
        var secCtx = new FakeRdpSecurityContext();
        var provider = new RdpSessionProvider(eps, creds, secCtx, audit);
        return (provider, eps, creds, audit);
    }

    private static void SeedFixtures(InMemoryRdpEndpointResolver eps, InMemoryRdpCredentialRefResolver creds)
    {
        eps.Add(new Endpoint
        {
            Id = EndpointId,
            AssetId = "asset-1",
            Protocol = RemoteProtocol.Rdp,
            Ipv4 = "10.0.0.5",
            Port = 0,
        });

        creds.Add(new CredentialRef
        {
            Id = CredRefId,
            Name = "Test Cred",
            Type = "password",
            SecretEnvelopeId = "env-1",
            Metadata = new CredentialMetadata { Username = "CORP\\admin" },
        });
    }

    private static SessionRequest MakeRequest(string? sessionId = null) => new()
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString("n"),
        Protocol = RemoteProtocol.Rdp,
        EndpointId = EndpointId,
        CredentialRefId = CredRefId,
        PreferIpv6 = false,
    };

    [Fact]
    public void Protocol_ReturnsRdp()
    {
        var (provider, _, _, _) = Build();
        Assert.Equal(RemoteProtocol.Rdp, provider.Protocol);
    }

    [Fact]
    public async Task OpenAsync_ReturnsOpenHandle()
    {
        var (provider, eps, creds, _) = Build();
        SeedFixtures(eps, creds);

        var handle = await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        Assert.True(handle.IsOpen);
        Assert.Equal(RemoteProtocol.Rdp, handle.Protocol);
        Assert.Equal(EndpointId, handle.EndpointId);
    }

    [Fact]
    public async Task OpenAsync_BuildsConnectionConfig_WithDefaultPortAndResolvedUsername()
    {
        var (provider, eps, creds, _) = Build();
        SeedFixtures(eps, creds);
        var request = MakeRequest();

        await provider.OpenAsync(request, CancellationToken.None);
        var config = provider.GetConnectionConfig(request.SessionId);

        Assert.Equal("10.0.0.5", config.Host);
        Assert.Equal(3389, config.Port);
        Assert.Equal("CORP\\admin", config.Username);
        Assert.True(config.NlaRequired);
        Assert.False(config.Redirection.ClipboardRedirectionEnabled);
    }

    [Fact]
    public async Task OpenAsync_EmitsSessionOpenedAudit()
    {
        var (provider, eps, creds, audit) = Build();
        SeedFixtures(eps, creds);
        var request = MakeRequest();

        await provider.OpenAsync(request, CancellationToken.None);

        var ev = Assert.Single(audit.Events);
        Assert.Equal(RdpActions.SessionOpened, ev.Action);
        Assert.Equal(request.SessionId, ev.SessionId);
        Assert.Equal("10.0.0.5", ev.Host);
        Assert.Equal("test-user", ev.UserId);
    }

    [Fact]
    public async Task OpenAsync_MissingUsername_Throws()
    {
        var (provider, eps, creds, _) = Build();
        eps.Add(new Endpoint { Id = EndpointId, AssetId = "a", Protocol = RemoteProtocol.Rdp, Ipv4 = "10.0.0.5" });
        creds.Add(new CredentialRef { Id = CredRefId, Name = "x", Type = "password", SecretEnvelopeId = "env-1" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAsync(MakeRequest(), CancellationToken.None));
    }

    [Fact]
    public void GetConnectionConfig_BeforeOpen_Throws()
    {
        var (provider, _, _, _) = Build();
        Assert.Throws<InvalidOperationException>(() => provider.GetConnectionConfig("never-opened"));
    }

    [Fact]
    public async Task CloseAsync_ClosesHandleAndEmitsSessionClosedAudit()
    {
        var (provider, eps, creds, audit) = Build();
        SeedFixtures(eps, creds);
        var request = MakeRequest();
        var handle = await provider.OpenAsync(request, CancellationToken.None);

        await provider.CloseAsync(handle, CancellationToken.None);

        Assert.False(handle.IsOpen);
        Assert.Equal(2, audit.Events.Count);
        Assert.Equal(RdpActions.SessionClosed, audit.Events[1].Action);
        Assert.Equal("10.0.0.5", audit.Events[1].Host);
    }

    [Fact]
    public async Task CloseAsync_UnknownHandle_DoesNotThrowOrAudit()
    {
        var (provider, _, _, audit) = Build();
        var fakeHandle = new SessionHandle
        {
            SessionId = "never-opened",
            Protocol = RemoteProtocol.Rdp,
            EndpointId = EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };

        await provider.CloseAsync(fakeHandle, CancellationToken.None);

        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task RdpSessionProvider_HasNoVaultDependency_AuditNeverContainsSecretLikeFields()
    {
        // Regressão estrutural: RdpSessionProvider não recebe IRdpCredentialResolver/IVault
        // no construtor — não há rota possível para um segredo entrar no RdpAuditEvent.
        var (provider, eps, creds, audit) = Build();
        SeedFixtures(eps, creds);

        await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        foreach (var ev in audit.Events)
        {
            Assert.DoesNotContain("s3cr3t", ev.ToString());
            Assert.DoesNotContain("s3cr3t", ev.UserId ?? string.Empty);
            Assert.DoesNotContain("s3cr3t", ev.CertificateThumbprint ?? string.Empty);
        }
    }
}
