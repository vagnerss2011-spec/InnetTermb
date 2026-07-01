using System.Collections.Generic;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelTests
{
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
}
