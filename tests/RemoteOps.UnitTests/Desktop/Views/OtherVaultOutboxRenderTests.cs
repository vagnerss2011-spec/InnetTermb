using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Renderização REAL do aviso "ficou coisa parada no outro cofre" na barra do shell.
///
/// <para><b>Afirma visibilidade EFETIVA e TEXTO</b> — nunca "não lançou". Binding quebrado no WPF não
/// lança: cai no valor padrão, e o padrão de <c>Visibility</c> é <c>Visible</c>. Um teste fraco
/// passaria justamente com o aviso quebrado — aceso e vazio. Elemento ausente do XAML conta como
/// invisível, então "o aviso não existe" e "o aviso está escondido" falham do mesmo jeito, que é o
/// que interessa a quem olha o monitor.</para>
/// </summary>
public sealed class OtherVaultOutboxRenderTests
{
    private static SessionLauncher NewLauncher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private static BrowserViewModel NewBrowser()
    {
        var store = new InMemoryLocalStore();
        var hosts = new HostsViewModel(store, NewLauncher(), "ws-local");
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local", "ws-local");
        return new BrowserViewModel(hosts, keychain, new LogsViewModel());
    }

    private sealed record Probe(bool Visible, string Text, IReadOnlyList<string> VisibleTexts);

    private static Probe RenderAndProbe(BrowserViewModel browser)
    {
        Probe probe = new(false, string.Empty, []);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var view = new BrowserView { DataContext = browser };
            var window = new Window
            {
                Content = view,
                Width = 1200,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var texto = view.FindName("OtherVaultOutboxText") as TextBlock;
                probe = new Probe(
                    Visible: IsEffectivelyVisible(texto),
                    Text: texto?.Text ?? string.Empty,
                    VisibleTexts: [.. VisibleTexts(window)]);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        return probe;
    }

    private static bool IsEffectivelyVisible(FrameworkElement? element)
    {
        if (element is null)
        {
            return false;
        }

        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible })
            {
                continue;
            }

            if (child is TextBlock tb)
            {
                yield return tb.Text;
            }

            foreach (string nested in VisibleTexts(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// <b>O aviso aparece, com o número e o cofre.</b> É a única coisa que separa "não subiu" de "o
    /// operador acha que subiu" — e o texto desenhado é comparado com o da VM, byte a byte: um
    /// binding para propriedade errada deixaria o aviso aceso e VAZIO, e passaria num teste que só
    /// olhasse visibilidade.
    /// </summary>
    [Fact]
    public void ComFilaParadaNoOutroCofre_OAvisoAPARECE_ComOTextoDaVM()
    {
        BrowserViewModel browser = NewBrowser();
        browser.OtherVaultOutbox.Apply(pendingPersonal: 12, pendingTeam: 0, checkFailed: false);

        Probe probe = RenderAndProbe(browser);

        Assert.True(probe.Visible);
        Assert.Equal(browser.OtherVaultOutbox.Text, probe.Text);
        Assert.Contains("12", probe.Text, StringComparison.Ordinal);
        Assert.Contains("cofre pessoal", probe.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(probe.Text, probe.VisibleTexts);
    }

    /// <summary>
    /// <b>A metade que denuncia binding quebrado.</b> Sem nada parado, o aviso fica COLLAPSED. O
    /// padrão de <c>Visibility</c> é <c>Visible</c>: um binding que não resolve deixaria este aviso
    /// aceso para sempre, e um teste que só checasse o caso ruim nunca perceberia.
    /// </summary>
    [Fact]
    public void SemFilaParada_OAvisoNaoAPARECE()
    {
        BrowserViewModel browser = NewBrowser();
        browser.OtherVaultOutbox.Apply(pendingPersonal: 0, pendingTeam: 0, checkFailed: false);

        Probe probe = RenderAndProbe(browser);

        Assert.False(probe.Visible);
    }

    /// <summary>"Não deu para conferir" também é desenhado — silêncio aqui seria a falha muda.</summary>
    [Fact]
    public void NaoVerificado_APARECE_NaBarra()
    {
        BrowserViewModel browser = NewBrowser();
        browser.OtherVaultOutbox.Apply(pendingPersonal: 0, pendingTeam: 0, checkFailed: true);

        Probe probe = RenderAndProbe(browser);

        Assert.True(probe.Visible);
        Assert.Equal(browser.OtherVaultOutbox.Text, probe.Text);
        Assert.Contains("verific", probe.Text, StringComparison.OrdinalIgnoreCase);
    }
}
