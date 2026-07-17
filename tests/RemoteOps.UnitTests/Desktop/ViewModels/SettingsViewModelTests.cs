using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelTests
{
    private sealed class StubMfaApi : IMfaApi
    {
        public Task<MfaEnrollResponse> EnrollAsync(CancellationToken ct = default)
            => Task.FromResult(new MfaEnrollResponse("SECRET", "otpauth://totp/x"));
        public Task ConfirmAsync(MfaConfirmRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisableAsync(MfaDisableRequest request, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeStore : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = new();
        private AppSettings _current;
        public FakeStore(AppSettings current) => _current = current;
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) { _current = settings; Saved = settings; }
    }

    [Fact]
    public void Ctor_LoadsFlagsFromStore()
    {
        var store = new FakeStore(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.RdpEnabled] = true },
        });

        var vm = new SettingsViewModel(store);

        Assert.True(vm.RdpEnabled);
        Assert.False(vm.NdeskEnabled);
    }

    [Fact]
    public void Save_PersistsToggledFlags_AndRaisesSaved()
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store);
        bool raised = false;
        vm.Saved += (_, _) => raised = true;

        vm.NdeskEnabled = true;
        vm.SaveCommand.Execute(null);

        Assert.True(store.Saved.Flags[FeatureFlagNames.NdeskEnabled]);
        Assert.True(raised);
    }

    [Fact]
    public void CheckForUpdates_Disabled_WhenNoUpdateService()
    {
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), updateService: null);
        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));
    }

    [Fact]
    public void ManageMfa_Disabled_AndCanManageFalse_WhenNoMfaApi()
    {
        // Modo local (sem conta na nuvem): a seção de 2FA fica desabilitada.
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()));

        Assert.False(vm.CanManageMfa);
        Assert.Null(vm.MfaApi);
        Assert.False(vm.ManageMfaCommand.CanExecute(null));
    }

    [Fact]
    public void ManageMfa_Enabled_AndRaisesRequest_WhenMfaApiPresent()
    {
        var api = new StubMfaApi();
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), mfaApi: api);
        bool requested = false;
        vm.MfaSetupRequested += (_, _) => requested = true;

        Assert.True(vm.CanManageMfa);
        Assert.Same(api, vm.MfaApi);
        Assert.True(vm.ManageMfaCommand.CanExecute(null));

        vm.ManageMfaCommand.Execute(null);
        Assert.True(requested);
    }
}
