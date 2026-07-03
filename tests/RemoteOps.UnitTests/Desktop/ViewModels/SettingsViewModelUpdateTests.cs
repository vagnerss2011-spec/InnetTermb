using System.IO;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Update;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelUpdateTests
{
    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateCheckResult NextResult = UpdateCheckResultFactory.Create(
            AppVersion.Parse("1.1.1"), AppVersion.Parse("1.2.0"), minimumRequiredVersion: null);
        public UpdateCheckResult? Applied;
        public bool ThrowOnApply;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
            => Task.FromResult(NextResult);

        public Task ApplyUpdateAsync(UpdateCheckResult update, CancellationToken ct = default)
        {
            if (ThrowOnApply) throw new InvalidOperationException("rede caiu");
            Applied = update;
            return Task.CompletedTask;
        }
    }

    private static JsonSettingsStore TempStore()
        => new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "s.json"));

    [Fact]
    public async Task Check_WithUpdate_EnablesApply_AndSaysSo()
    {
        var svc = new FakeUpdateService();
        var vm = new SettingsViewModel(TempStore(), svc);
        Assert.False(vm.ApplyUpdateCommand.CanExecute(null));

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.UpdateAvailable);
        Assert.True(vm.ApplyUpdateCommand.CanExecute(null));
        Assert.Contains("1.2.0", vm.UpdateStatus);
    }

    [Fact]
    public async Task Check_NoUpdate_KeepsApplyDisabled()
    {
        var svc = new FakeUpdateService
        {
            NextResult = UpdateCheckResultFactory.Create(
                AppVersion.Parse("1.2.0"), AppVersion.Parse("1.2.0"), minimumRequiredVersion: null),
        };
        var vm = new SettingsViewModel(TempStore(), svc);

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.UpdateAvailable);
        Assert.False(vm.ApplyUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_CallsService_WithCheckedResult()
    {
        var svc = new FakeUpdateService();
        var vm = new SettingsViewModel(TempStore(), svc);
        await vm.CheckForUpdatesAsync();

        await vm.ApplyUpdateAsync();

        Assert.NotNull(svc.Applied);
        Assert.Equal(AppVersion.Parse("1.2.0"), svc.Applied!.AvailableVersion);
    }

    [Fact]
    public async Task Apply_Failure_ShowsActionableStatus()
    {
        var svc = new FakeUpdateService { ThrowOnApply = true };
        var vm = new SettingsViewModel(TempStore(), svc);
        await vm.CheckForUpdatesAsync();

        await vm.ApplyUpdateAsync();

        Assert.Contains("Não foi possível", vm.UpdateStatus);
    }
}
