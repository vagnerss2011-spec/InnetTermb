using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.Update;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class WorkspaceViewModelUpdateTests
{
    private sealed class FakeUpdateService : IUpdateService
    {
        public bool ThrowOnCheck;
        public bool ThrowOnApply;
        public UpdateCheckResult? Applied;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
            => ThrowOnCheck
                ? throw new InvalidOperationException("offline")
                : Task.FromResult(UpdateCheckResultFactory.Create(
                    AppVersion.Parse("1.1.1"), AppVersion.Parse("1.2.0"), minimumRequiredVersion: null));

        public Task ApplyUpdateAsync(UpdateCheckResult update, CancellationToken ct = default)
        {
            if (ThrowOnApply) throw new InvalidOperationException("rede caiu");
            Applied = update;
            return Task.CompletedTask;
        }
    }

    private static WorkspaceViewModel Build(IUpdateService? svc)
    {
        var store = new InMemoryLocalStore();
        var logs = new LogsViewModel();
        var hosts = new HostsViewModel(store, new SessionLauncher(new TabsViewModel(), null, null, null, null, null, null), "ws-local");
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local");
        var browser = new BrowserViewModel(hosts, keychain, logs);
        return new WorkspaceViewModel(browser, new TabsViewModel(), updateService: svc);
    }

    [Fact]
    public async Task QuietCheck_NoService_ReturnsNull()
        => Assert.Null(await Build(null).CheckForUpdatesQuietAsync());

    [Fact]
    public async Task QuietCheck_ServiceThrows_ReturnsNull()
        => Assert.Null(await Build(new FakeUpdateService { ThrowOnCheck = true }).CheckForUpdatesQuietAsync());

    [Fact]
    public async Task QuietCheck_ReturnsResult()
    {
        var result = await Build(new FakeUpdateService()).CheckForUpdatesQuietAsync();
        Assert.NotNull(result);
        Assert.True(result!.UpdateAvailable);
    }

    [Fact]
    public async Task TryApply_Success_CallsService()
    {
        var svc = new FakeUpdateService();
        var vm = Build(svc);
        var check = await vm.CheckForUpdatesQuietAsync();

        bool ok = await vm.TryApplyUpdateAsync(check!);

        Assert.True(ok);
        Assert.NotNull(svc.Applied);
    }

    [Fact]
    public async Task TryApply_Failure_ReturnsFalse()
    {
        var svc = new FakeUpdateService { ThrowOnApply = true };
        var vm = Build(svc);
        var check = await vm.CheckForUpdatesQuietAsync();

        Assert.False(await vm.TryApplyUpdateAsync(check!));
    }
}
