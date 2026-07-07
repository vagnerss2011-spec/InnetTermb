using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using RemoteOps.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Prova o comportamento central do <see cref="TabControlEx"/> (keep-alive): trocar a aba
/// selecionada e voltar NÃO recria o conteúdo — reutiliza o MESMO ContentPresenter. Era a raiz do
/// bug de campo "o terminal fica preto / parece que fechou ao clicar em Hosts": o TabControl padrão
/// destruía e recriava a TerminalTabView (WebView2 + xterm) a cada troca de aba. Com keep-alive, a
/// View (e portanto a sessão SSH e o histórico do terminal) sobrevive à troca de aba.
/// </summary>
public sealed class TabControlExTests
{
    private const string TemplateXaml =
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'" +
        " xmlns:local='clr-namespace:RemoteOps.Desktop;assembly=RemoteOps.Desktop'" +
        " TargetType='{x:Type local:TabControlEx}'>" +
        "  <Grid>" +
        "    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='*'/></Grid.RowDefinitions>" +
        "    <TabPanel Grid.Row='0' IsItemsHost='True'/>" +
        "    <Grid x:Name='PART_ItemsHolder' Grid.Row='1'/>" +
        "  </Grid>" +
        "</ControlTemplate>";

    [Fact]
    public void SwitchingSelectionAwayAndBack_ReusesSameContentPresenter()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var template = (ControlTemplate)XamlReader.Parse(TemplateXaml);
            var tc = new TabControlEx { Template = template };
            object a = new();
            object b = new();
            tc.ItemsSource = new[] { a, b };

            var window = new Window
            {
                Content = tc,
                Width = 300,
                Height = 200,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                tc.SelectedItem = a;
                window.UpdateLayout();
                ContentPresenter? presenterForA = FindPresenter(tc, a);

                tc.SelectedItem = b; // sai da aba A
                window.UpdateLayout();
                tc.SelectedItem = a; // volta para a aba A
                window.UpdateLayout();

                ContentPresenter? presenterForAAgain = FindPresenter(tc, a);

                Assert.NotNull(presenterForA);
                // MESMA instância = a View não foi destruída/recriada ao trocar de aba.
                Assert.Same(presenterForA, presenterForAAgain);
                // Só a aba ativa fica visível; a outra continua VIVA porém oculta.
                Assert.Equal(Visibility.Visible, presenterForAAgain!.Visibility);
                Assert.Equal(Visibility.Collapsed, FindPresenter(tc, b)!.Visibility);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }

    private static ContentPresenter? FindPresenter(TabControlEx tc, object content)
    {
        var holder = (Panel)tc.Template.FindName("PART_ItemsHolder", tc);
        foreach (object child in holder.Children)
        {
            if (child is ContentPresenter cp && Equals(cp.Content, content))
            {
                return cp;
            }
        }

        return null;
    }
}
