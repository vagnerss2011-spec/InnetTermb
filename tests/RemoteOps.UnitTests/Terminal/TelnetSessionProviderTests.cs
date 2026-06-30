using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal;
using RemoteOps.Terminal.Telnet;
using RemoteOps.UnitTests.Terminal.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class TelnetSessionProviderTests
{
    private static readonly string EndpointId = "ep-tel-1";

    private static (TelnetSessionProvider provider,
                    FakeTelnetConnectionFactory factory,
                    FakeTelnetConsentProvider consent,
                    InMemoryTerminalAuditSink audit)
        Build(bool consents)
    {
        var factory = new FakeTelnetConnectionFactory();
        var consent = new FakeTelnetConsentProvider(consents);
        var audit = new InMemoryTerminalAuditSink();
        var secCtx = new FakeTerminalSecurityContext();
        var endpoints = new InMemoryEndpointResolver();
        endpoints.Add(new Endpoint
        {
            Id = EndpointId,
            AssetId = "asset-2",
            Protocol = RemoteProtocol.Telnet,
            Ipv4 = "10.0.0.1",
            Port = 23,
        });

        var provider = new TelnetSessionProvider(endpoints, consent, audit, secCtx, factory);
        return (provider, factory, consent, audit);
    }

    private static SessionRequest MakeRequest(string? sessionId = null) => new()
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString(),
        Protocol = RemoteProtocol.Telnet,
        EndpointId = EndpointId,
        CredentialRefId = "n/a",
        PreferIpv6 = false,
        Terminal = new TerminalOptions { Cols = 80, Rows = 24 },
    };

    // ── testes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Protocol_ReturnsTelnet()
    {
        var (provider, _, _, _) = Build(consents: true);
        Assert.Equal(RemoteProtocol.Telnet, provider.Protocol);
    }

    [Fact]
    public async Task OpenAsync_WithoutConsent_DoesNotConnectAndThrows()
    {
        var (provider, factory, consent, _) = Build(consents: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAsync(MakeRequest(), CancellationToken.None));

        // FIX 2: não deve ter criado nenhuma conexão TCP
        Assert.Empty(factory.Created);
        Assert.Equal(1, consent.CallCount);
    }

    [Fact]
    public async Task OpenAsync_WithConsent_CreatesHandleWithIsOpenTrue()
    {
        var (provider, _, _, _) = Build(consents: true);

        var handle = await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        Assert.True(handle.IsOpen);
        Assert.Equal(RemoteProtocol.Telnet, handle.Protocol);
    }

    [Fact]
    public async Task OpenAsync_WithConsent_AuditsConsentAndSessionOpened()
    {
        var (provider, _, _, audit) = Build(consents: true);

        await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        Assert.Contains(audit.Events, e => e.Action == TerminalActions.TelnetConsentGranted);
        Assert.Contains(audit.Events, e => e.Action == TerminalActions.SessionOpened);
    }

    [Fact]
    public async Task ResizeAsync_SendsNawsPacket()
    {
        var (provider, factory, _, _) = Build(consents: true);

        var sessionId = "tel-resize";
        await provider.OpenAsync(MakeRequest(sessionId), CancellationToken.None);

        var handle = new SessionHandle
        {
            SessionId = sessionId,
            Protocol = RemoteProtocol.Telnet,
            EndpointId = EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };

        await provider.ResizeAsync(handle, 160, 48);

        // IAC SB NAWS cols-hi cols-lo rows-hi rows-lo IAC SE (RFC 855)
        byte[] expectedNaws = [0xFF, 0xFA, 0x1F, 0x00, 0xA0, 0x00, 0x30, 0xFF, 0xF0];
        var conn = factory.Created[0];
        var sentToServer = conn.WrittenToServer.SelectMany(b => b).ToArray();
        Assert.Equal(expectedNaws, sentToServer);
    }

    [Fact]
    public async Task WriteAndReadAsync_RoundTrip()
    {
        var (provider, factory, _, _) = Build(consents: true);

        var sessionId = "tel-rw";
        await provider.OpenAsync(MakeRequest(sessionId), CancellationToken.None);

        var handle = new SessionHandle
        {
            SessionId = sessionId,
            Protocol = RemoteProtocol.Telnet,
            EndpointId = EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };

        var conn = factory.Created[0];

        // Injetar dados puros (sem IAC) no stream fake
        byte[] testData = [0x41, 0x42, 0x43]; // "ABC"
        await conn.InjectStream.WriteAsync(testData);
        conn.InjectStream.Close();

        var received = new List<byte>();
        await foreach (var chunk in provider.ReadAsync(handle, CancellationToken.None))
            received.AddRange(chunk.ToArray());

        Assert.Equal(testData, received.ToArray());
    }

    [Fact]
    public async Task CloseAsync_SetsIsOpenFalseAndAudits()
    {
        var (provider, _, _, audit) = Build(consents: true);

        var sessionId = "tel-close";
        await provider.OpenAsync(MakeRequest(sessionId), CancellationToken.None);

        var handle = new SessionHandle
        {
            SessionId = sessionId,
            Protocol = RemoteProtocol.Telnet,
            EndpointId = EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };

        audit.Clear();
        await provider.CloseAsync(handle, CancellationToken.None);

        Assert.False(handle.IsOpen);
        Assert.Contains(audit.Events, e => e.Action == TerminalActions.SessionClosed);
    }
}
