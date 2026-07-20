using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.Update;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Renderização REAL (thread STA + tema de produção) do indicador de atualização na barra de status,
/// nos DOIS estados. Screenshot não funciona nesta máquina, e esta é justamente a classe de bug que
/// passa no build e quebra na tela: um <c>DynamicResource</c> com nome errado compila e só aparece em
/// runtime — aconteceu durante esta própria implementação (<c>Brush.Accent</c> não existe; o certo é
/// <c>Brush.Accent.Base</c>).
/// </summary>
public sealed class UpdateIndicatorRenderTests
{
    private sealed class StubUpdateService : IUpdateService
    {
        private readonly bool _hasUpdate;

        public StubUpdateService(bool hasUpdate) => _hasUpdate = hasUpdate;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
            => Task.FromResult(UpdateCheckResultFactory.Create(
                AppVersion.Parse("1.4.1"),
                AppVersion.Parse(_hasUpdate ? "1.4.2" : "1.4.1"),
                minimumRequiredVersion: null));

        public Task ApplyUpdateAsync(UpdateCheckResult update, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static SessionLauncher NewLauncher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private static BrowserViewModel NewBrowser(UpdateNotificationViewModel update)
    {
        var store = new InMemoryLocalStore();
        var hosts = new HostsViewModel(store, NewLauncher(), "ws-local");
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local");
        return new BrowserViewModel(hosts, keychain, new LogsViewModel(), update: update);
    }

    private static Exception? RenderShell(BrowserViewModel browser)
        => StaThreadRunner.Run(() =>
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

    [Fact]
    public async Task Renders_With_Update_Available()
    {
        var update = new UpdateNotificationViewModel(new StubUpdateService(hasUpdate: true));
        await update.CheckAsync();
        Assert.True(update.HasUpdate); // pré-condição: é o estado VISÍVEL que queremos renderizar

        Assert.Null(RenderShell(NewBrowser(update)));
    }

    [Fact]
    public async Task Renders_Without_Update()
    {
        var update = new UpdateNotificationViewModel(new StubUpdateService(hasUpdate: false));
        await update.CheckAsync();
        Assert.False(update.HasUpdate);

        Assert.Null(RenderShell(NewBrowser(update)));
    }

    [Fact]
    public void Renders_Before_Any_Check()
    {
        // Estado inicial do app: a barra sobe antes da primeira verificação terminar.
        Assert.Null(RenderShell(NewBrowser(new UpdateNotificationViewModel(updateService: null))));
    }
}
