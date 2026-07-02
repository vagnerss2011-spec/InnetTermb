using System.Collections.Generic;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Terminal;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Sessions;

public sealed class SessionLauncherTests
{
    private sealed class FakeTerminalProvider : ITerminalSessionProvider
    {
        public string Protocol => "ssh";

        public Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct) =>
            Task.FromResult(new SessionHandle
            {
                SessionId = request.SessionId,
                Protocol = request.Protocol,
                EndpointId = request.EndpointId,
                OpenedAt = DateTimeOffset.UtcNow,
                IsOpen = true,
            });

        public Task CloseAsync(SessionHandle handle, CancellationToken ct) => Task.CompletedTask;

        public Task WriteAsync(SessionHandle handle, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
            SessionHandle handle,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task ResizeAsync(SessionHandle handle, int cols, int rows, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static Asset AssetWith(params string[] protocols)
    {
        var eps = new List<Endpoint>();
        foreach (var p in protocols)
            eps.Add(new Endpoint { Id = "e-" + p, AssetId = "a1", Protocol = p, Ipv4 = "10.0.0.1", Port = 22, CredentialRefId = "c1" });
        return new Asset { Id = "a1", WorkspaceId = "ws-local", Name = "r1", Endpoints = eps };
    }

    [Fact]
    public void PrimaryProtocol_PrefersSshThenTelnetThenRdp()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        Assert.Equal("ssh", l.PrimaryProtocol(AssetWith("telnet", "ssh")));
        Assert.Equal("telnet", l.PrimaryProtocol(AssetWith("rdp", "telnet")));
        Assert.Equal("rdp", l.PrimaryProtocol(AssetWith("rdp")));
    }

    [Fact]
    public async Task LaunchAsync_Ssh_OpensTerminalTab()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, new FakeTerminalProvider(), null, null, null);

        await l.LaunchAsync(AssetWith("ssh"), "ssh");

        Assert.True(tabs.HasTabs);
    }

    [Fact]
    public void CanLaunch_Rdp_RequiresFlag()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        // sem flag rdp.enabled e sem provider → não pode
        Assert.False(l.CanLaunch(AssetWith("rdp"), "rdp"));
    }
}
