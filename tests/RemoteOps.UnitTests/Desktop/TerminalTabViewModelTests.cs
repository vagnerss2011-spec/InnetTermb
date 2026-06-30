using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Terminal;
using RemoteOps.UnitTests.Desktop.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Testes unitários de TerminalTabViewModel — ciclo de vida de sessão, pump de saída,
/// proteção contra race condition e invariantes de estado.
/// Não há WebView2, rede real, ou credenciais; tudo via FakeTerminalSessionProvider.
/// </summary>
public sealed class TerminalTabViewModelTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static (TerminalTabViewModel vm, FakeTerminalSessionProvider provider) Build()
    {
        var provider = new FakeTerminalSessionProvider();
        var request = new SessionRequest
        {
            SessionId = "test-session-1",
            Protocol = "ssh",
            EndpointId = "ep-1",
            CredentialRefId = "cr-1",
        };
        var vm = new TerminalTabViewModel(
            id: "tab-1",
            title: "router-01 (SSH)",
            protocol: "ssh",
            provider: provider,
            baseRequest: request);

        return (vm, provider);
    }

    // ── ConnectAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_OpensSession_AndTransitionsToConnected()
    {
        var (vm, provider) = Build();

        provider.CompleteOutput(); // pump termina imediatamente
        await vm.ConnectAsync(120, 30);

        Assert.True(vm.IsConnected);
        Assert.Single(provider.OpenedRequests);
    }

    [Fact]
    public async Task ConnectAsync_PassesTerminalOptions_ToProvider()
    {
        var (vm, provider) = Build();
        provider.CompleteOutput();

        await vm.ConnectAsync(cols: 200, rows: 50);

        var req = provider.OpenedRequests[0];
        Assert.Equal(200, req.Terminal!.Cols);
        Assert.Equal(50, req.Terminal!.Rows);
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnecting_SecondCallIsIgnored()
    {
        var (vm, provider) = Build();

        // Connect mas não completar o pump ainda para que o estado 1 (connecting) seja breve.
        // Duas chamadas concorrentes: apenas uma deve abrir sessão.
        var t1 = vm.ConnectAsync(80, 24);
        var t2 = vm.ConnectAsync(80, 24); // deve ser ignorada silenciosamente

        provider.CompleteOutput();
        await Task.WhenAll(t1, t2);

        // Apenas uma sessão aberta (não duas)
        Assert.Single(provider.OpenedRequests);
    }

    [Fact]
    public async Task ConnectAsync_WhenProviderThrows_ResetsToIdleState()
    {
        var (vm, provider) = Build();
        provider.ShouldThrowOnOpen = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ConnectAsync(80, 24));

        Assert.False(vm.IsConnected);
    }

    // ── SendInputAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task SendInputAsync_WhenNotConnected_ReturnsWithoutWrite()
    {
        var (vm, provider) = Build();

        await vm.SendInputAsync("hello"u8.ToArray());

        Assert.Empty(provider.WrittenData);
    }

    [Fact]
    public async Task SendInputAsync_WhenConnected_ForwardsToProvider()
    {
        var (vm, provider) = Build();
        provider.CompleteOutput();
        await vm.ConnectAsync(80, 24);

        byte[] input = "ls -la\r"u8.ToArray();
        await vm.SendInputAsync(input);

        Assert.Single(provider.WrittenData);
        Assert.Equal(input, provider.WrittenData[0].ToArray());
    }

    // ── ResizeAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ResizeAsync_WhenNotConnected_ReturnsWithoutCall()
    {
        var (vm, provider) = Build();

        await vm.ResizeAsync(120, 40);

        Assert.Empty(provider.ResizeCalls);
    }

    [Fact]
    public async Task ResizeAsync_WhenConnected_ForwardsColsAndRows()
    {
        var (vm, provider) = Build();
        provider.CompleteOutput();
        await vm.ConnectAsync(80, 24);

        await vm.ResizeAsync(180, 50);

        Assert.Single(provider.ResizeCalls);
        Assert.Equal((180, 50), provider.ResizeCalls[0]);
    }

    // ── OutputReceived / pump ─────────────────────────────────────────────────

    [Fact]
    public async Task OutputReceived_IsFired_ForEachChunkFromProvider()
    {
        var (vm, provider) = Build();
        var received = new List<byte[]>();
        vm.OutputReceived += chunk => received.Add(chunk.ToArray());

        await vm.ConnectAsync(80, 24);

        byte[] chunk1 = "Hello"u8.ToArray();
        byte[] chunk2 = " World"u8.ToArray();
        provider.EnqueueOutput(chunk1);
        provider.EnqueueOutput(chunk2);
        provider.CompleteOutput();

        // Aguarda o pump drenar (fire-and-forget com task não rastreada — polling curto)
        for (int i = 0; i < 50 && received.Count < 2; i++)
            await Task.Delay(10);

        Assert.Equal(2, received.Count);
        Assert.Equal(chunk1, received[0]);
        Assert.Equal(chunk2, received[1]);
    }

    [Fact]
    public async Task PumpEnd_ResetsIsConnected_ToFalse()
    {
        var (vm, provider) = Build();
        await vm.ConnectAsync(80, 24);
        provider.CompleteOutput(); // encerra o pump

        for (int i = 0; i < 50 && vm.IsConnected; i++)
            await Task.Delay(10);

        Assert.False(vm.IsConnected);
    }

    // ── CloseAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CloseAsync_WhenNotConnected_IsNoop()
    {
        var (vm, provider) = Build();

        await vm.CloseAsync(); // não deve lançar

        Assert.Equal(0, provider.CloseCount);
    }

    [Fact]
    public async Task CloseAsync_CancelsSessionAndCallsProviderClose()
    {
        var (vm, provider) = Build();
        await vm.ConnectAsync(80, 24);

        await vm.CloseAsync();

        Assert.Equal(1, provider.CloseCount);
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public async Task CloseAsync_CalledTwice_IsIdempotent()
    {
        var (vm, provider) = Build();
        await vm.ConnectAsync(80, 24);

        await vm.CloseAsync();
        await vm.CloseAsync(); // segunda chamada não deve lançar nem chamar CloseAsync duas vezes

        Assert.Equal(1, provider.CloseCount);
    }

    // ── Herança / títulos ─────────────────────────────────────────────────────

    [Fact]
    public void Title_AndProtocol_AreAccessibleFromBaseClass()
    {
        var (vm, _) = Build();

        Assert.Equal("router-01 (SSH)", vm.Title);
        Assert.Equal("ssh", vm.Protocol);
        Assert.Equal("SSH", vm.ProtocolLabel);
    }
}
