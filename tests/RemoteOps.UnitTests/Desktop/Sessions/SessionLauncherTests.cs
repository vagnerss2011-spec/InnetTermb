using System.Collections.Generic;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.MikroTik;
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

    private sealed class ThrowingWinBoxRunner : IWinBoxRunner
    {
        public Task<string> LaunchAsync(ExternalToolLaunchRequest request, CancellationToken ct = default)
            => throw new WinBoxValidationException("manifesto sem sha256 valido");
    }

    private sealed class FakeExternalTerminalLauncher : IExternalTerminalLauncher
    {
        public SshLaunchTarget? Last { get; private set; }
        public System.Exception? ThrowOnLaunch { get; init; }

        public Task LaunchSshAsync(SshLaunchTarget target, CancellationToken ct = default)
        {
            if (ThrowOnLaunch != null)
            {
                throw ThrowOnLaunch;
            }
            Last = target;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredentialResolver : ICredentialRefResolver
    {
        public Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default)
            => Task.FromResult(new CredentialRef
            {
                Id = credentialRefId,
                Name = "cred",
                Type = "password",
                Metadata = new CredentialMetadata { Username = "admin" },
            });
    }

    private static Asset AssetWith(params string[] protocols) => AssetWithCred(credentialRefId: "c1", protocols);

    private static Asset AssetWithCred(string? credentialRefId, params string[] protocols)
    {
        var eps = new List<Endpoint>();
        foreach (var p in protocols)
            eps.Add(new Endpoint { Id = "e-" + p, AssetId = "a1", Protocol = p, Ipv4 = "10.0.0.1", Port = 22, CredentialRefId = credentialRefId });
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
    public async Task LaunchAsync_Ssh_OpensTerminalTab_AndSucceeds()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, new FakeTerminalProvider(), null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWith("ssh"), "ssh");

        Assert.True(result.Success);
        Assert.True(tabs.HasTabs);
    }

    [Fact]
    public async Task LaunchAsync_NoEndpointForProtocol_FailsWithMessage()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, new FakeTerminalProvider(), null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWith("ssh"), "telnet");

        Assert.False(result.Success);
        Assert.Contains("TELNET", result.Error);
        Assert.False(tabs.HasTabs);
    }

    [Fact]
    public async Task LaunchAsync_Ssh_WithoutCredential_FailsAndDoesNotOpenDeadTab()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, new FakeTerminalProvider(), null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWithCred(credentialRefId: null, "ssh"), "ssh");

        Assert.False(result.Success);
        Assert.Contains("credencial", result.Error);
        Assert.False(tabs.HasTabs);
    }

    [Fact]
    public async Task LaunchAsync_Ssh_ProviderMissing_Fails()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, null, null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWith("ssh"), "ssh");

        Assert.False(result.Success);
        Assert.Contains("indisponível", result.Error);
        Assert.False(tabs.HasTabs);
    }

    [Fact]
    public async Task LaunchAsync_MikroTik_RunnerMissing_Fails()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWith("mikrotik"), "mikrotik");

        Assert.False(result.Success);
        Assert.Contains("WinBox", result.Error);
    }

    [Fact]
    public async Task LaunchAsync_MikroTik_ValidationException_SurfacesReason()
    {
        var l = new SessionLauncher(new TabsViewModel(), new ThrowingWinBoxRunner(), null, null, null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWith("mikrotik"), "mikrotik");

        Assert.False(result.Success);
        Assert.Contains("sha256", result.Error);
    }

    [Fact]
    public async Task LaunchAsync_Rdp_FlagOff_FailsWithGuidance()
    {
        var tabs = new TabsViewModel();
        var l = new SessionLauncher(tabs, null, null, null, null, null, null);

        LaunchResult result = await l.LaunchAsync(AssetWith("rdp"), "rdp");

        Assert.False(result.Success);
        Assert.Contains("RDP", result.Error);
        Assert.False(tabs.HasTabs);
    }

    [Fact]
    public void CanLaunch_Rdp_RequiresFlag()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        // sem flag rdp.enabled e sem provider → não pode
        Assert.False(l.CanLaunch(AssetWith("rdp"), "rdp"));
    }

    [Fact]
    public async Task LaunchAsync_Ssh_WithExternalLauncher_OpensExternalTerminal_NoWebViewTab()
    {
        var tabs = new TabsViewModel();
        var ext = new FakeExternalTerminalLauncher();
        var l = new SessionLauncher(tabs, null, null, new FakeTerminalProvider(), null, null, null,
            new FakeCredentialResolver(), ext);

        LaunchResult result = await l.LaunchAsync(AssetWith("ssh"), "ssh");

        Assert.True(result.Success);
        Assert.False(tabs.HasTabs); // externo NÃO abre aba WebView2
        Assert.NotNull(ext.Last);
        Assert.Equal("10.0.0.1", ext.Last!.Host);
        Assert.Equal(22, ext.Last.Port);
        Assert.Equal("admin", ext.Last.Username);
    }

    [Fact]
    public async Task LaunchAsync_Ssh_External_SshExeMissing_FailsWithGuidance()
    {
        var ext = new FakeExternalTerminalLauncher { ThrowOnLaunch = new System.ComponentModel.Win32Exception(2) };
        var l = new SessionLauncher(new TabsViewModel(), null, null, new FakeTerminalProvider(), null, null, null,
            new FakeCredentialResolver(), ext);

        LaunchResult result = await l.LaunchAsync(AssetWith("ssh"), "ssh");

        Assert.False(result.Success);
        Assert.Contains("OpenSSH", result.Error);
    }

    [Fact]
    public async Task CanLaunch_Ssh_TrueWhenOnlyExternalLauncherPresent()
    {
        var l = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null,
            new FakeCredentialResolver(), new FakeExternalTerminalLauncher());
        Assert.True(l.CanLaunch(AssetWith("ssh"), "ssh"));
        await Task.CompletedTask;
    }
}
