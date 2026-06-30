using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Rdp;
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
}
