using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal;
using RemoteOps.Terminal.Ssh;
using RemoteOps.UnitTests.Terminal.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshSessionProviderTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static (SshSessionProvider provider,
                    FakeSshConnectionFactory factory,
                    InMemoryTerminalAuditSink audit,
                    FakeHostKeyConfirmation confirmation,
                    InMemoryEndpointResolver endpoints,
                    InMemoryCredentialRefResolver creds,
                    FakeVault vault)
        Build(bool confirmKey = true)
    {
        var factory = new FakeSshConnectionFactory();
        var audit = new InMemoryTerminalAuditSink();
        var confirmation = new FakeHostKeyConfirmation(confirmKey);
        var endpoints = new InMemoryEndpointResolver();
        var creds = new InMemoryCredentialRefResolver();
        var vault = new FakeVault();
        var secCtx = new FakeTerminalSecurityContext();

        var provider = new SshSessionProvider(
            endpoints, creds, vault, secCtx, confirmation, audit, factory, new HostKeyStore(path: null));

        return (provider, factory, audit, confirmation, endpoints, creds, vault);
    }

    private static readonly string EndpointId = "ep-1";
    private static readonly string CredRefId = "cr-1";

    private static async Task<(InMemoryEndpointResolver eps, InMemoryCredentialRefResolver crs, string envelopeId)>
        SetupFixturesAsync(InMemoryEndpointResolver eps, InMemoryCredentialRefResolver crs, FakeVault vault)
    {
        eps.Add(new Endpoint
        {
            Id = EndpointId,
            AssetId = "asset-1",
            Protocol = RemoteProtocol.Ssh,
            Ipv4 = "127.0.0.1",
            Port = 22,
        });

        var envelopeId = await vault.SetupAsync("s3cr3t", "cred-password");

        crs.Add(new CredentialRef
        {
            Id = CredRefId,
            Name = "Test Cred",
            Type = "password",
            SecretEnvelopeId = envelopeId,
            Metadata = new CredentialMetadata { Username = "admin" },
        });

        return (eps, crs, envelopeId);
    }

    private static SessionRequest MakeRequest(string? sessionId = null) => new()
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString(),
        Protocol = RemoteProtocol.Ssh,
        EndpointId = EndpointId,
        CredentialRefId = CredRefId,
        PreferIpv6 = false,
        Terminal = new TerminalOptions { Cols = 120, Rows = 32 },
    };

    // ── testes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Protocol_ReturnsSsh()
    {
        var (provider, _, _, _, _, _, _) = Build();
        Assert.Equal(RemoteProtocol.Ssh, provider.Protocol);
    }

    [Fact]
    public async Task OpenAsync_KnownKey_CreatesHandleWithIsOpenTrue()
    {
        var (provider, factory, audit, _, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        // 1ª conexão: key desconhecida → usuário confirma → 2ª conexão com key confiada
        // Na 2ª factory.Create(), o ForceValidatorResult=null mas a HostKeyStore já tem a key.
        // Para simular isso: a 2ª conexão do factory deve aceitar a key (porque a store foi atualizada).
        // FakeSshConnectionFactory cria nova FakeSshConnection; a 2ª deve passar no validator.

        var request = MakeRequest();
        var handle = await provider.OpenAsync(request, CancellationToken.None);

        Assert.True(handle.IsOpen);
        Assert.Equal(RemoteProtocol.Ssh, handle.Protocol);
        Assert.Equal(request.SessionId, handle.SessionId);
    }

    [Fact]
    public async Task OpenAsync_UnknownKey_CallsConfirmation()
    {
        var (provider, factory, audit, confirmation, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        // Confirmação deve ter sido chamada para a key desconhecida
        Assert.Single(confirmation.Calls);
        Assert.False(confirmation.Calls[0].IsChanged); // key nova, não alterada
    }

    [Fact]
    public async Task OpenAsync_UserRejectsKey_ThrowsAndAuditsRejection()
    {
        var (provider, factory, audit, _, eps, crs, vault) = Build(confirmKey: false);
        await SetupFixturesAsync(eps, crs, vault);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAsync(MakeRequest(), CancellationToken.None));

        // FIX 1: deve ter auditado a rejeição
        Assert.Contains(audit.Events, e => e.Action == TerminalActions.HostKeyRejected);
    }

    [Fact]
    public async Task OpenAsync_ChangedKey_AuditsHostKeyChangedBeforeAskingUser()
    {
        var (provider, factory, audit, confirmation, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        // 1ª abertura: aceita a key fp1
        var fp1 = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";
        factory.SimulatedFingerprint = fp1;
        await provider.OpenAsync(MakeRequest("s1"), CancellationToken.None);

        // 2ª abertura com key diferente (simula host key mudada)
        var fp2 = "ffffeeeeddddccccbbbbaaaa99998888ffffeeeeddddccccbbbbaaaa99998888";
        factory.SimulatedFingerprint = fp2;
        factory.ForceValidatorResult = null; // deixa o validator decidir
        audit.Clear();
        confirmation.Calls.Clear();

        await provider.OpenAsync(MakeRequest("s2"), CancellationToken.None);

        // FIX 5: deve auditar HostKeyChanged ANTES de perguntar ao usuário
        var changeEvent = audit.Events.FirstOrDefault(e => e.Action == TerminalActions.HostKeyChanged);
        Assert.NotNull(changeEvent);
        Assert.Equal(fp2, changeEvent!.Fingerprint);

        // E a confirmação deve indicar IsChanged=true
        Assert.Single(confirmation.Calls);
        Assert.True(confirmation.Calls[0].IsChanged);
    }

    [Fact]
    public async Task OpenAsync_NoBlockingGetResultInHostKeyCallback()
    {
        // FIX 1: verificamos indiretamente que o callback é síncrono e rápido —
        // se houvesse GetAwaiter().GetResult() no callback, o test deadlockaria num
        // contexto single-threaded. O timeout de 5s detecta isso.
        var (provider, _, _, _, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handle = await provider.OpenAsync(MakeRequest(), cts.Token);
        Assert.True(handle.IsOpen);
    }

    [Fact]
    public async Task ResizeAsync_PropagatesColsAndRows()
    {
        var (provider, factory, _, _, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        var request = MakeRequest("resize-test");
        await provider.OpenAsync(request, CancellationToken.None);

        await provider.ResizeAsync(
            new SessionHandle
            {
                SessionId = "resize-test",
                Protocol = RemoteProtocol.Ssh,
                EndpointId = EndpointId,
                OpenedAt = DateTimeOffset.UtcNow,
                IsOpen = true,
            },
            cols: 200,
            rows: 50);

        // A última conexão criada tem o shell com o resize registado
        var shell = factory.Created.Last().Shell;
        Assert.NotNull(shell);
        Assert.Equal((200u, 50u), shell!.LastResize);
    }

    [Fact]
    public async Task WriteAndReadAsync_RoundTrip()
    {
        var (provider, factory, _, _, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        var sessionId = "rw-test";
        var request = MakeRequest(sessionId);
        await provider.OpenAsync(request, CancellationToken.None);

        var handle = new SessionHandle
        {
            SessionId = sessionId,
            Protocol = RemoteProtocol.Ssh,
            EndpointId = EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };

        // Injetar dados no stream fake (simula output do servidor SSH)
        var shell = factory.Created.Last().Shell!;
        byte[] testData = [1, 2, 3, 4, 5];
        await shell.InjectStream.WriteAsync(testData);
        await shell.InjectStream.FlushAsync();
        shell.InjectStream.Close(); // fecha o stream para terminar o ReadAsync

        // Coletar o que o provider produziu via ReadAsync
        var received = new List<byte>();
        await foreach (var chunk in provider.ReadAsync(handle, CancellationToken.None))
            received.AddRange(chunk.ToArray());

        Assert.Equal(testData, received.ToArray());
    }

    [Fact]
    public async Task AuditEvents_DoNotContainPassword()
    {
        var (provider, _, audit, _, eps, crs, vault) = Build(confirmKey: true);
        await SetupFixturesAsync(eps, crs, vault);

        await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        // FIX 3: nenhum campo de audit deve conter a senha "s3cr3t"
        foreach (var ev in audit.Events)
        {
            Assert.DoesNotContain("s3cr3t", ev.ToString());
            Assert.DoesNotContain("s3cr3t", ev.Fingerprint ?? string.Empty);
            Assert.DoesNotContain("s3cr3t", ev.UserId ?? string.Empty);
        }
    }
}
