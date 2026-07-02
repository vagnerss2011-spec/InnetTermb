using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class BrowserViewModelTests
{
    private static BrowserViewModel Build()
    {
        var store = new InMemoryLocalStore();
        var hosts = new HostsViewModel(store, new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null), "ws-local");
        return new BrowserViewModel(hosts, new KeychainViewModel(store, new FakeVault(), "ws-local"), new LogsViewModel());
    }

    [Fact]
    public void ShowKeychain_SwitchesSection()
    {
        var vm = Build();
        Assert.True(vm.IsHosts);
        vm.ShowKeychainCommand.Execute(null);
        Assert.True(vm.IsKeychain);
        Assert.False(vm.IsHosts);
    }

    [Fact]
    public void OpenSettings_RaisesEvent()
    {
        var vm = Build();
        bool raised = false;
        vm.SettingsRequested += (_, _) => raised = true;
        vm.OpenSettingsCommand.Execute(null);
        Assert.True(raised);
    }
}
