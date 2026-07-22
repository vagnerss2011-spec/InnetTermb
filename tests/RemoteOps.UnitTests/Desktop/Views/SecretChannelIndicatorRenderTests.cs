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
/// Renderização REAL do aviso do canal de SENHAS na barra de status.
///
/// <para><b>Afirma VISIBILIDADE e TEXTO</b> — nunca "não lançou". Binding quebrado no WPF NÃO lança:
/// cai no valor padrão, e o padrão de <c>Visibility</c> é <c>Visible</c>. Um teste fraco passaria
/// justamente com o indicador quebrado: aceso e VAZIO.</para>
///
/// <para>É este controle que transforma o <c>SecretSyncSkip</c> em algo que alguém enxerga. Sem ele,
/// a guarda de raiz divergente (1j) seria decorativa: o envelope não seria gravado e nada na tela
/// diria por quê.</para>
/// </summary>
public sealed class SecretChannelIndicatorRenderTests
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
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local", "ws-local");
        return new BrowserViewModel(hosts, keychain, new LogsViewModel(), sync: sync);
    }

    private static (Exception? Error, Visibility Visibility, string Text) RenderAndInspect(
        SecretChannelState estado)
    {
        var sync = new SyncStatusViewModel(new NoopController());
        sync.Apply(new SyncStatus(SyncState.Synced, ConflictCount: 0, estado));
        BrowserViewModel browser = NewBrowser(sync);

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

                var indicator = (StackPanel?)view.FindName("SecretChannelIndicator");
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

    /// <summary>
    /// Canal degradado: o aviso está VISÍVEL e o texto fala de SENHA. "Sincronizado" sozinho faria o
    /// operador acreditar que a senha do cliente está lá.
    /// </summary>
    [Fact]
    public void CanalDegradado_ApareceNaBarra_ComTextoSobreSenha()
    {
        (Exception? error, Visibility visibility, string text) =
            RenderAndInspect(SecretChannelState.Degraded);

        Assert.True(error is null, error?.ToString());
        Assert.Equal(Visibility.Visible, visibility);
        Assert.Contains("senha", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanalNoChao_ApareceNaBarra()
    {
        (Exception? error, Visibility visibility, string text) =
            RenderAndInspect(SecretChannelState.Failed);

        Assert.True(error is null, error?.ToString());
        Assert.Equal(Visibility.Visible, visibility);
        Assert.Contains("senha", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Canal saudável: COLLAPSED. É a metade que denuncia binding quebrado — o padrão de
    /// <c>Visibility</c> é <c>Visible</c>, então um binding que não resolve deixaria o aviso aceso
    /// para sempre, e um teste que só checasse o caso ruim nunca perceberia.
    /// </summary>
    [Fact]
    public void CanalSaudavel_NaoAparece()
    {
        (Exception? error, Visibility visibility, _) = RenderAndInspect(SecretChannelState.Healthy);

        Assert.True(error is null, error?.ToString());
        Assert.Equal(Visibility.Collapsed, visibility);
    }
}
