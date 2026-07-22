using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Credentials;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop.ViewModels;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// O indicador de cofre no SHELL (Fatia 1e), renderizado de verdade (STA + tema de produção).
///
/// <para><b>Por que ele fica no shell e não só na tela de Equipe:</b> o erro caro desta fatia é
/// cadastrar o equipamento de um cliente achando que está no cofre do time. Esse erro acontece na
/// tela de Hosts, não na de Equipe — um aviso que só aparece lá chega tarde demais. Por isso o teste
/// afirma que o texto está VISÍVEL na barra do shell, e não apenas que a VM tem a propriedade.</para>
/// </summary>
public sealed class VaultBadgeRenderTests
{
    private static SessionLauncher NewLauncher() =>
        new(new TabsViewModel(), winBox: null, flags: null, ssh: null, telnet: null, rdp: null, rdpCred: null);

    private static BrowserViewModel NewBrowser()
    {
        var store = new InMemoryLocalStore();
        var hosts = new HostsViewModel(store, NewLauncher(), "ws-local");
        var keychain = new KeychainViewModel(store, new FakeVault(), "ws-local");
        return new BrowserViewModel(hosts, keychain, new LogsViewModel());
    }

    private sealed record Probe(
        IReadOnlyList<string> VisibleTexts,
        bool BadgeVisible,
        string BadgeText,
        bool WarningIconVisible);

    private static Probe RenderShellAndProbe(BrowserViewModel browser)
    {
        Probe probe = new([], false, string.Empty, false);

        Exception? error = StaThreadRunner.Run(() =>
        {
            var view = new BrowserView { DataContext = browser };
            var window = new Window
            {
                Content = view,
                Width = 1000,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                var badge = view.FindName("VaultBadgeText") as TextBlock;
                var warn = view.FindName("VaultBadgeWarningIcon") as FrameworkElement;

                probe = new Probe(
                    VisibleTexts: [.. VisibleTexts(window)],
                    BadgeVisible: IsEffectivelyVisible(badge),
                    BadgeText: badge?.Text ?? string.Empty,
                    WarningIconVisible: IsEffectivelyVisible(warn));
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
    /// Sem conta na nuvem o indicador continua na tela: "cofre pessoal, só neste PC" também é uma
    /// informação que o operador precisa ter, e um indicador que só aparece às vezes não é indicador.
    /// </summary>
    [Fact]
    public void SemConta_OIndicadorAparece_DizendoQueECofrePessoal()
    {
        Probe probe = RenderShellAndProbe(NewBrowser());

        Assert.True(probe.BadgeVisible);
        Assert.Contains("pessoal", probe.BadgeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(probe.BadgeText, probe.VisibleTexts);
        Assert.False(probe.WarningIconVisible);
    }

    /// <summary>Cofre pessoal com conta: presente e sem alarme falso.</summary>
    [Fact]
    public void CofrePessoal_ApareceSemAlarme()
    {
        BrowserViewModel browser = NewBrowser();
        browser.Vault.Apply(VaultScope.Personal);

        Probe probe = RenderShellAndProbe(browser);

        Assert.True(probe.BadgeVisible);
        Assert.False(probe.WarningIconVisible);
    }

    /// <summary>
    /// <b>O estado que precisa saltar aos olhos.</b> Workspace de time com o cofre pessoal ativo: o
    /// texto muda E o sinal de alerta aparece. Sem o ícone, o aviso teria a mesma cara apagada do
    /// estado normal — e a lição da v1.4.2 desta base é que aviso que não é notado falhou.
    /// </summary>
    [Fact]
    public void WorkspaceDeTime_ComCofrePessoal_ACENDE_OAlerta()
    {
        BrowserViewModel browser = NewBrowser();
        browser.Vault.Apply(VaultScope.TeamPending);

        Probe probe = RenderShellAndProbe(browser);

        Assert.True(probe.BadgeVisible);
        Assert.True(probe.WarningIconVisible);
        Assert.Equal(browser.Vault.Label, probe.BadgeText);
        Assert.Contains("time", probe.BadgeText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Estado "não confirmado" também é desenhado — silêncio aqui seria a falha muda.</summary>
    [Fact]
    public void NaoConfirmado_ApareceNaBarra()
    {
        BrowserViewModel browser = NewBrowser();
        browser.Vault.Apply(VaultScope.Unconfirmed);

        Probe probe = RenderShellAndProbe(browser);

        Assert.True(probe.BadgeVisible);
        Assert.Contains("confirm", probe.BadgeText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// O título da janela principal carrega o cofre. É o único lugar que continua visível com uma
    /// sessão SSH em tela cheia — e o que aparece no Alt+Tab e na barra de tarefas.
    /// </summary>
    [Fact]
    public void TituloDaJanelaPrincipal_CarregaOCofre()
    {
        var browser = NewBrowser();
        browser.Vault.Apply(VaultScope.TeamPending);
        var workspace = new WorkspaceViewModel(browser, new TabsViewModel());

        var store = new InMemoryLocalStore();
        string titulo = string.Empty;
        Exception? error = StaThreadRunner.Run(() =>
        {
            var window = new RemoteOps.Desktop.MainWindow(
                workspace, store, new InlineCredentialService(store, new FakeVault()))
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                titulo = window.Title;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(error is null, error?.ToString());
        Assert.Contains("RemoteOps", titulo, StringComparison.Ordinal);
        Assert.Equal(browser.Vault.WindowTitle, titulo);
        Assert.Contains("time", titulo, StringComparison.OrdinalIgnoreCase);
    }
}
