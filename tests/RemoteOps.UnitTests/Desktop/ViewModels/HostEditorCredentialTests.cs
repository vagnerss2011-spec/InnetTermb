using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorCredentialTests
{
    [Fact]
    public async Task AddEndpoint_WithCredential_SetsCredentialRefId()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef { Id = "c1", Name = "root", Type = "password", SecretEnvelopeId = "e1" });
        var vm = new HostEditorViewModel(store, "ws-local", existing: null, groupId: null);
        await vm.LoadCredentialsAsync();
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointPort = 22;
        vm.NewEndpointCredentialId = "c1";
        vm.AddEndpointCommand.Execute(null);
        Assert.Equal("c1", vm.Endpoints.Single().CredentialRefId);
        Assert.Contains(vm.AvailableCredentials, c => c.Id == "c1");
    }
}
