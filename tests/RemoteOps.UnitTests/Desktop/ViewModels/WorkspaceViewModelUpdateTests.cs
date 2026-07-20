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
        public bool ThrowOnApply;
        public UpdateCheckResult? Applied;

        // Só serve para produzir o UpdateCheckResult que os testes de APLICAÇÃO precisam ter em mãos;
        // o comportamento de checagem em si é coberto em UpdateNotificationViewModelTests.
        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
            => Task.FromResult(UpdateCheckResultFactory.Create(
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

    // Os testes de CheckForUpdatesQuietAsync saíram junto com o método: a verificação passou a ser
    // responsabilidade do UpdateNotificationViewModel (que também guarda o estado do indicador e o
    // carimbo da última checagem boa) e está coberta em UpdateNotificationViewModelTests. Manter dois
    // caminhos de checagem daria duas fontes de verdade. A APLICAÇÃO continua sendo daqui — e continua
    // coberta pelos dois testes abaixo.

    [Fact]
    public async Task TryApply_Success_CallsService()
    {
        var svc = new FakeUpdateService();
        var vm = Build(svc);
        UpdateCheckResult check = await svc.CheckForUpdatesAsync();

        bool ok = await vm.TryApplyUpdateAsync(check);

        Assert.True(ok);
        Assert.NotNull(svc.Applied);
    }

    [Fact]
    public async Task TryApply_Failure_ReturnsFalse()
    {
        var svc = new FakeUpdateService { ThrowOnApply = true };
        var vm = Build(svc);
        UpdateCheckResult check = await svc.CheckForUpdatesAsync();

        Assert.False(await vm.TryApplyUpdateAsync(check));
    }
}
