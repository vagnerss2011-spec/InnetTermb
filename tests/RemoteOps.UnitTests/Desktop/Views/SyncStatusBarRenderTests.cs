using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Fase 2, item B: renderização REAL (thread STA + tema de produção) da barra de status de sync no
/// shell (BrowserView), em cada estado e nos dois modos (com nuvem = botão habilitado; sem nuvem =
/// desabilitado). Screenshot não funciona nesta máquina — o render test é a rede de segurança contra
/// bug de XAML que passa no build e só quebra no layout (DataTrigger/StaticResource/binding).
/// </summary>
public sealed class SyncStatusBarRenderTests
{
    private sealed class NoopController : ISyncController
    {
        public Task<bool> SyncNowAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);

        public Task DismissConflictsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static SessionLauncher NewLauncher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private static BrowserViewModel NewBrowser(SyncStatusViewModel sync)
    {
        var store = new InMemoryLocalStore();
        var hosts = new HostsViewModel(store, NewLauncher(), "ws-local");
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local");
        return new BrowserViewModel(hosts, keychain, new LogsViewModel(), sync: sync);
    }

    private static void RenderShell(BrowserViewModel browser)
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var view = new BrowserView { DataContext = browser };
            var window = new Window
            {
                Content = view,
                Width = 900,
                Height = 600,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    [Theory]
    [InlineData(SyncState.Offline)]
    [InlineData(SyncState.Syncing)]
    [InlineData(SyncState.Synced)]
    [InlineData(SyncState.Error)]
    public void StatusBar_WithCloud_Renders_In_Each_State(SyncState state)
    {
        var sync = new SyncStatusViewModel(new NoopController());
        sync.Apply(new SyncStatus(state, state == SyncState.Synced ? 2 : 0));

        RenderShell(NewBrowser(sync));
    }

    [Fact]
    public void StatusBar_WithoutCloud_Renders_Disabled()
    {
        // Sem controlador: HasCloud = false, botão desabilitado — offline-first no shell.
        RenderShell(NewBrowser(new SyncStatusViewModel()));
    }

    // O indicador de canal é controle NOVO na barra: tem que passar pelo layout real nos dois textos.
    // Bug de XAML aqui (binding/StaticResource) compila liso e só aparece na tela do operador.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StatusBar_Renders_Channel_Indicator(bool realTime)
    {
        var sync = new SyncStatusViewModel(new NoopController());
        sync.Apply(new SyncStatus(SyncState.Synced));
        sync.SetRealTime(realTime);

        RenderShell(NewBrowser(sync));
    }
}
