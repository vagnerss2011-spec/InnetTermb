using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostsViewModelLaunchTests
{
    private static AssetViewModel HostWith(params string[] protocols)
    {
        var eps = new List<Endpoint>();
        foreach (var p in protocols)
            eps.Add(new Endpoint { Id = "e-" + p, AssetId = "a1", Protocol = p, Ipv4 = "10.0.0.1", Port = 22, CredentialRefId = null });
        return new AssetViewModel(new Asset { Id = "a1", WorkspaceId = "ws-local", Name = "r1", Endpoints = eps });
    }

    [Fact]
    public async Task Connect_WhenLaunchFails_RaisesLaunchFailedWithMessage()
    {
        // Launcher sem providers: ssh sem credencial → falha com mensagem.
        var launcher = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        var vm = new HostsViewModel(new InMemoryLocalStore(), launcher, "ws-local");
        string? error = null;
        vm.LaunchFailed += (_, msg) => error = msg;

        await vm.ConnectAsync(HostWith("ssh"), "ssh");

        Assert.NotNull(error);
        Assert.Contains("indisponível", error);
    }

    [Fact]
    public async Task Connect_WrongProtocol_RaisesLaunchFailed()
    {
        var launcher = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        var vm = new HostsViewModel(new InMemoryLocalStore(), launcher, "ws-local");
        string? error = null;
        vm.LaunchFailed += (_, msg) => error = msg;

        await vm.ConnectAsync(HostWith("ssh"), "mikrotik");

        Assert.NotNull(error);
        Assert.Contains("MIKROTIK", error);
    }

    [Fact]
    public async Task Delete_WhenCredRevokeFails_RaisesLaunchFailed_NotSilent()
    {
        var launcher = new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null);
        var vm = new HostsViewModel(new InMemoryLocalStore(), launcher, "ws-local", new ThrowingInlineCredentialService())
        {
            SelectedHost = HostWith("ssh"), // asset com 1 endpoint → DeleteForEndpointAsync é chamado
        };
        string? error = null;
        vm.LaunchFailed += (_, msg) => error = msg;

        vm.DeleteHostCommand.Execute(null); // fire-and-forget
        for (int i = 0; i < 50 && error is null; i++) await Task.Delay(10);

        Assert.NotNull(error);
        Assert.Contains("excluir", error);
    }

    // Serviço de credencial inline que SEMPRE falha — simula cofre/DB indisponível na exclusão.
    private sealed class ThrowingInlineCredentialService : IInlineCredentialService
    {
        public Task<string> CreateForEndpointAsync(string endpointId, string username, char[] password, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DeleteForEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
            => throw new InvalidOperationException("cofre indisponível");
        public bool IsInlineScope(string? scope) => false;
    }
}
