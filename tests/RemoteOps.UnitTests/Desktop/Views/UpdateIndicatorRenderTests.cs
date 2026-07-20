using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

    /// <summary>
    /// Renderiza o shell e devolve o Button do indicador encontrado na árvore visual REAL — não a VM.
    /// É a diferença entre "não estourou" e "o operador enxerga".
    /// </summary>
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

                var indicator = (Button?)view.FindName("UpdateIndicator");
                Assert.NotNull(indicator); // se sumiu do XAML, o teste tem de falhar, não passar vazio
                visibility = indicator.Visibility;
                text = string.Concat(FindTexts(indicator));
            }
            finally
            {
                window.Close();
            }
        });

        return (error, visibility, text);
    }

    private static IEnumerable<string> FindTexts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb)
            {
                yield return tb.Text;
            }

            foreach (string nested in FindTexts(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// O teste que teria pego o bug de escopo de DataContext. A barra de status vive num Border com
    /// <c>DataContext="{Binding Sync}"</c>; um binding "Update.X" ali resolve contra o
    /// SyncStatusViewModel e falha EM SILÊNCIO — o WPF não lança, só não mostra. Afirmar
    /// "não estourou" passava com o indicador invisível; por isso aqui se afirma VISIBILIDADE e TEXTO.
    /// </summary>
    [Fact]
    public async Task Indicator_Is_Actually_Visible_When_Update_Is_Available()
    {
        var update = new UpdateNotificationViewModel(new StubUpdateService(hasUpdate: true));
        await update.CheckAsync();
        Assert.True(update.HasUpdate); // pré-condição na VM

        var (error, visibility, text) = RenderAndInspect(NewBrowser(update));

        Assert.Null(error);
        Assert.Equal(Visibility.Visible, visibility);
        Assert.Contains("1.4.2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indicator_Is_Hidden_Without_Update()
    {
        var update = new UpdateNotificationViewModel(new StubUpdateService(hasUpdate: false));
        await update.CheckAsync();
        Assert.False(update.HasUpdate);

        var (error, visibility, _) = RenderAndInspect(NewBrowser(update));

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, visibility);
    }

    [Fact]
    public void Renders_Before_Any_Check()
    {
        // Estado inicial do app: a barra sobe antes da primeira verificação terminar.
        var (error, visibility, _) = RenderAndInspect(NewBrowser(new UpdateNotificationViewModel(updateService: null)));

        Assert.Null(error);
        Assert.NotEqual(Visibility.Visible, visibility);
    }
}
