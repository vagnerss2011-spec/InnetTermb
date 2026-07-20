using System;
using System.Collections.Generic;
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
/// Renderização REAL do aviso "N alterações não subiram" na barra de status, nos dois estados.
///
/// <para>Afirma VISIBILIDADE e TEXTO — não apenas "não lançou". Falha de binding no WPF não lança: cai
/// no valor padrão, e o padrão de <c>Visibility</c> é <c>Visible</c>, então um teste fraco passaria
/// justamente com o indicador quebrado (aceso e vazio). Foi exatamente o que aconteceu com o indicador
/// de atualização na v1.4.2.</para>
/// </summary>
public sealed class ConflictIndicatorRenderTests
{
    private sealed class FakeController : ISyncController
    {
        public Task SyncNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SyncConflictItem>> GetConflictsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SyncConflictItem>>(
                [new SyncConflictItem("asset", "h1", DateTimeOffset.Now, "version_mismatch")]);

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

    private static (Exception? Error, Visibility Visibility, string Text) RenderAndInspect(BrowserViewModel browser)
    {
        Visibility visibility = Visibility.Collapsed;
        string text = string.Empty;

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

                var indicator = (Button?)view.FindName("ConflictIndicator");
                Assert.NotNull(indicator);
                visibility = indicator.Visibility;
                text = string.Concat(Texts(indicator));
            }
            finally
            {
                window.Close();
            }
        });

        return (error, visibility, text);
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                yield return tb.Text;
            }

            foreach (string nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void Indicator_Is_Visible_And_Explains_When_There_Are_Conflicts()
    {
        var sync = new SyncStatusViewModel(new FakeController());
        sync.Apply(new SyncStatus(SyncState.Synced, 18));

        var (error, visibility, text) = RenderAndInspect(NewBrowser(sync));

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, visibility);
        Assert.Contains("18 alterações não subiram", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Indicator_Is_Hidden_Without_Conflicts()
    {
        var sync = new SyncStatusViewModel(new FakeController());
        sync.Apply(new SyncStatus(SyncState.Synced, 0));

        var (error, visibility, _) = RenderAndInspect(NewBrowser(sync));

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, visibility);
    }

    [Fact]
    public void Conflicts_Window_Renders_With_Items()
    {
        var sync = new SyncStatusViewModel(new FakeController());
        sync.Apply(new SyncStatus(SyncState.Synced, 1));

        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new SyncConflictsWindow(sync)
            {
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

        Assert.Null(error);
    }
}
