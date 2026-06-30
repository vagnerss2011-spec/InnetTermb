using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Rdp;
using RemoteOps.UnitTests.Desktop.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Rdp;

public sealed class RdpTabViewModelTests
{
    private static SessionRequest MakeRequest() => new()
    {
        SessionId = Guid.NewGuid().ToString("n"),
        Protocol = RemoteProtocol.Rdp,
        EndpointId = "ep-1",
        CredentialRefId = "cr-1",
    };

    [Fact]
    public async Task PrepareAsync_OpensSessionAndExposesConfig()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var request = MakeRequest();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, request);

        var config = await vm.PrepareAsync();

        Assert.Single(provider.OpenedRequests);
        Assert.Equal("10.0.0.5", config.Host);
        Assert.Equal("10.0.0.5", vm.ConnectionConfig!.Host);
    }

    [Fact]
    public async Task PrepareAsync_CalledTwiceConcurrently_OnlyOpensOnce()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await Task.WhenAll(vm.PrepareAsync(), vm.PrepareAsync());

        Assert.Single(provider.OpenedRequests);
    }

    [Fact]
    public async Task PrepareAsync_CalledTwiceConcurrently_BothCallersGetTheResolvedConfig()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        var results = await Task.WhenAll(vm.PrepareAsync(), vm.PrepareAsync());

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.Equal("10.0.0.5", results[0].Host);
        Assert.Equal("10.0.0.5", results[1].Host);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task PrepareAsync_CalledTwiceWhileGenuinelyRacing_SingleFlightStillHolds()
    {
        // Diferente do teste acima (cujo Task.WhenAll resolve a primeira chamada
        // de forma totalmente síncrona antes da segunda sequer começar — não há
        // janela de corrida real), este teste usa um gate em OpenAsync para
        // forçar as duas chamadas a PrepareAsync a se sobreporem de verdade
        // antes que qualquer uma complete, provando que o single-flight
        // (cache de _prepareTask) também segura sob concorrência genuína.
        var provider = new FakeRdpSessionProvider { OpenGate = new TaskCompletionSource<bool>() };
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        Task<RdpConnectionConfig> task1 = vm.PrepareAsync();
        Task<RdpConnectionConfig> task2 = vm.PrepareAsync();

        // Nenhuma das duas deve ter completado ainda — ambas presas no gate.
        Assert.False(task1.IsCompleted);
        Assert.False(task2.IsCompleted);

        provider.OpenGate.SetResult(true);
        var results = await Task.WhenAll(task1, task2);

        Assert.Single(provider.OpenedRequests);
        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.Equal("10.0.0.5", results[0].Host);
        Assert.Same(results[0], results[1]);
    }

    [Fact]
    public async Task PrepareAsync_OnFailure_ResetsStateForRetry()
    {
        var provider = new FakeRdpSessionProvider { ShouldThrowOnOpen = true };
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.PrepareAsync());

        provider.ShouldThrowOnOpen = false;
        await vm.PrepareAsync(); // não deve lançar "já conectando"
        Assert.Single(provider.OpenedRequests);
    }

    [Fact]
    public async Task ResolvePasswordAsync_DelegatesToCredentialResolver_WithRequestCredentialRefId()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver { PasswordToReturn = "s3cr3t-rdp" };
        var request = MakeRequest();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, request);

        string? password = await vm.ResolvePasswordAsync();

        Assert.Equal("s3cr3t-rdp", password);
        Assert.Equal([request.CredentialRefId], credResolver.RequestedCredentialRefIds);
    }

    [Fact]
    public void MarkConnected_SetsIsConnectedTrue()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        vm.MarkConnected();

        Assert.True(vm.IsConnected);
    }

    [Fact]
    public void MarkDisconnected_SetsIsConnectedFalse_AndRaisesConnectFailed()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());
        vm.MarkConnected();

        string? reason = null;
        vm.ConnectFailed += r => reason = r;
        vm.MarkDisconnected("network error");

        Assert.False(vm.IsConnected);
        Assert.Equal("network error", reason);
    }

    [Fact]
    public async Task PrepareAsync_AfterMarkDisconnected_ReOpensSession()
    {
        // Regressão: MarkDisconnected (caminho de retry de reconexão, disparado
        // pela View após ConnectFailed) precisa invalidar o cache de _prepareTask
        // — caso contrário um PrepareAsync subsequente reaproveita a config
        // antiga e nunca chama _provider.OpenAsync de novo.
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await vm.PrepareAsync();
        vm.MarkDisconnected("network error");
        await vm.PrepareAsync();

        Assert.Equal(2, provider.OpenedRequests.Count);
    }

    [Fact]
    public async Task CloseAsync_BeforePrepare_DoesNothing()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await vm.CloseAsync();

        Assert.Equal(0, provider.CloseCount);
    }

    [Fact]
    public async Task CloseAsync_AfterPrepare_ClosesProviderSession()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());
        await vm.PrepareAsync();

        await vm.CloseAsync();

        Assert.Equal(1, provider.CloseCount);
        Assert.False(vm.IsConnected);
    }

    [Fact]
    public async Task PrepareAsync_AfterCloseAsync_ReOpensSession()
    {
        // Regressão equivalente para o caminho de fechar/reabrir a aba: CloseAsync
        // também precisa invalidar o cache de _prepareTask.
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await vm.PrepareAsync();
        await vm.CloseAsync();
        await vm.PrepareAsync();

        Assert.Equal(2, provider.OpenedRequests.Count);
    }
}
