using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelCloudTests
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
    public void Ctor_LoadsCloudConfig_FromStore()
    {
        var store = new FakeStore(new AppSettings
        {
            CloudSyncEnabled = true,
            CloudServerUrl = "https://sync.exemplo.com",
        });

        var vm = new SettingsViewModel(store);

        Assert.True(vm.CloudSyncEnabled);
        Assert.Equal("https://sync.exemplo.com", vm.CloudServerUrl);
    }

    [Fact]
    public void Save_PersistsCloudConfig_Trimmed()
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store)
        {
            CloudSyncEnabled = true,
            CloudServerUrl = "  https://sync.exemplo.com  ",
        };

        vm.SaveCommand.Execute(null);

        Assert.True(store.Saved.CloudSyncEnabled);
        Assert.Equal("https://sync.exemplo.com", store.Saved.CloudServerUrl);
    }

    [Fact]
    public void Save_EmptyUrl_PersistsNull()
    {
        var store = new FakeStore(new AppSettings { CloudServerUrl = "https://x.com" });
        var vm = new SettingsViewModel(store) { CloudServerUrl = "   " };

        vm.SaveCommand.Execute(null);

        Assert.Null(store.Saved.CloudServerUrl);
    }

    [Fact]
    public void SaveAndRestart_ValidHttps_PersistsAndRaisesRestart()
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store)
        {
            CloudSyncEnabled = true,
            CloudServerUrl = "https://innetsync.innetsolutions.net.br",
        };
        bool restarted = false;
        vm.RestartRequested += (_, _) => restarted = true;

        vm.SaveAndRestartCommand.Execute(null);

        Assert.False(vm.HasCloudConfigError);
        Assert.True(restarted);
        Assert.True(store.Saved.CloudSyncEnabled);
        Assert.Equal("https://innetsync.innetsolutions.net.br", store.Saved.CloudServerUrl);
    }

    [Theory]
    [InlineData("http://inseguro.com")]
    [InlineData("nao-e-url")]
    [InlineData("")]
    public void SaveAndRestart_EnabledWithBadUrl_ShowsError_NoRestart(string bad)
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store) { CloudSyncEnabled = true, CloudServerUrl = bad };
        bool restarted = false;
        vm.RestartRequested += (_, _) => restarted = true;

        vm.SaveAndRestartCommand.Execute(null);

        Assert.True(vm.HasCloudConfigError);
        Assert.False(restarted);
    }

    [Fact]
    public void SaveAndRestart_Disabled_OkEvenWithoutUrl()
    {
        // Desligar a nuvem não exige URL: só reinicia pra voltar ao modo local.
        var store = new FakeStore(new AppSettings { CloudSyncEnabled = true, CloudServerUrl = "https://x.com" });
        var vm = new SettingsViewModel(store) { CloudSyncEnabled = false, CloudServerUrl = "" };
        bool restarted = false;
        vm.RestartRequested += (_, _) => restarted = true;

        vm.SaveAndRestartCommand.Execute(null);

        Assert.False(vm.HasCloudConfigError);
        Assert.True(restarted);
        Assert.False(store.Saved.CloudSyncEnabled);
    }
}
