using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.Sync.Remote;
using RemoteOps.UnitTests.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Renderização REAL do carimbo de retorno do "Sincronizar agora" na barra de status.
///
/// <para>Afirma VISIBILIDADE e TEXTO — não apenas "não lançou". Binding quebrado no WPF não lança: cai
/// no valor padrão, e o padrão de <c>Visibility</c> é <c>Visible</c>, então um teste fraco passaria
/// justamente com o carimbo aceso e VAZIO — que é o mesmo silêncio que este recurso existe pra acabar.</para>
///
/// <para><b>Atenção ao escopo:</b> o Border da barra está com <c>DataContext="{Binding Sync}"</c>, então
/// os bindings do carimbo são diretos (<c>SyncOutcomeText</c>), sem <c>RelativeSource</c>.</para>
/// </summary>
public sealed class SyncOutcomeStampRenderTests
{
    private sealed class FakeController : ISyncController
    {
        public bool Result { get; init; } = true;

        public Task<bool> SyncNowAsync(CancellationToken ct = default) => Task.FromResult(Result);

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>([]);

        public Task DismissConflictsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Relógio fixo pra a hora exibida não depender da máquina nem do instante do teste.</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 21, 9, 5, 3, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
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

    private static (Exception? Error, Visibility Visibility, string Text, Brush? Foreground)
        RenderAndInspect(BrowserViewModel browser)
    {
        Visibility visibility = Visibility.Collapsed;
        string text = string.Empty;
        Brush? foreground = null;

        Exception? error = StaThreadRunner.Run(() =>
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

                var stamp = (FrameworkElement?)view.FindName("SyncOutcomeIndicator");
                Assert.NotNull(stamp);
                visibility = stamp.Visibility;

                var label = (TextBlock?)view.FindName("SyncOutcomeLabel");
                Assert.NotNull(label);
                text = label.Text;
                foreground = label.Foreground;
            }
            finally
            {
                window.Close();
            }
        });

        return (error, visibility, text, foreground);
    }

    /// <summary>Roda o clique e espera o ciclo terminar — o comando é fire-and-forget.</summary>
    private static async Task ClickSyncNow(SyncStatusViewModel sync)
    {
        sync.SyncNowCommand.Execute(null);

        var sw = Stopwatch.StartNew();
        while (sync.IsBusy && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(10);
        }

        Assert.False(sync.IsBusy, "o ciclo não terminou no tempo esperado");
    }

    [Fact]
    public void Stamp_Is_Hidden_Before_The_First_Click()
    {
        var sync = new SyncStatusViewModel(new FakeController(), new FixedClock());
        sync.Apply(new SyncStatus(SyncState.Synced));

        var (error, visibility, _, _) = RenderAndInspect(NewBrowser(sync));

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, visibility);
    }

    [Fact]
    public async Task Stamp_Is_Visible_With_The_Time_After_A_Successful_Click()
    {
        // O caso que originou a dúvida: já estava "Sincronizado", clicou, terminou "Sincronizado".
        var sync = new SyncStatusViewModel(new FakeController { Result = true }, new FixedClock());
        sync.Apply(new SyncStatus(SyncState.Synced));
        await ClickSyncNow(sync);

        var (error, visibility, text, _) = RenderAndInspect(NewBrowser(sync));

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, visibility);
        Assert.Equal("Última sincronização: 09:05:03", text);
    }

    [Fact]
    public async Task Stamp_Says_It_Failed_And_Is_Painted_As_Error()
    {
        var sync = new SyncStatusViewModel(new FakeController { Result = false }, new FixedClock());
        await ClickSyncNow(sync);

        var (error, visibility, text, foreground) = RenderAndInspect(NewBrowser(sync));

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, visibility);
        Assert.Equal("Não sincronizou às 09:05:03", text);

        // A cor é o que separa "deu" de "não deu" de relance; sem o DataTrigger o operador leria a
        // falha com a mesma cara discreta do sucesso.
        Brush expected = ErrorBrush();
        Assert.Equal(((SolidColorBrush)expected).Color, ((SolidColorBrush?)foreground)?.Color);
    }

    private static Brush ErrorBrush()
    {
        Brush? brush = null;
        Exception? error = StaThreadRunner.Run(
            () => brush = (Brush)Application.Current.Resources["Brush.Status.Error"]);

        Assert.Null(error);
        Assert.NotNull(brush);
        return brush;
    }
}
