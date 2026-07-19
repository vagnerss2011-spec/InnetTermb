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
        // "Salvar" global NÃO marca configurado — só "Aplicar e reiniciar" faz a GUI vencer a env var.
        Assert.False(store.Saved.CloudSyncConfigured);
    }

    [Fact]
    public void Save_ReloadsFromDisk_PreservingOtherWriters()
    {
        // Achado #19: Save relê do disco, não reverte o que outro gravador (ex.: MarkAllSeen da aba
        // Novidades) escreveu com a janela aberta.
        var store = new FakeStore(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        var vm = new SettingsViewModel(store);
        store.Save(new AppSettings { LastSeenChangelogVersion = "1.3.2" }); // disco muda com a janela aberta

        vm.SaveCommand.Execute(null);

        Assert.Equal("1.3.2", store.Saved.LastSeenChangelogVersion);
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
        Assert.True(store.Saved.CloudSyncConfigured); // "Aplicar e reiniciar" marca configurado
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
        Assert.True(store.Saved.CloudSyncConfigured); // desligar pela GUI também é "configurar" (fix #7)
    }
}
