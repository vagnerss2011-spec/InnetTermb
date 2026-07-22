using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class KeychainViewModelTests
{
    [Fact]
    public async Task LoadAsync_ListsCredentialRefs()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef { Id = "c1", Name = "root", Type = "password" });
        var vm = new KeychainViewModel(store, new FakeVault(), "ws-local", "ws-local");

        await vm.LoadAsync();

        Assert.Contains(vm.Credentials, c => c.Name == "root");
    }
}
