using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using RemoteOps.Desktop.NDesk;
using RemoteOps.UnitTests.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

/// <summary>
/// Complementa NDeskTabViewRenderTests: aqueles testes provam que renderizar o painel de
/// consentimento não derruba o processo (Mode=OneWay correto). Nenhum deles prova que o
/// consentimento continua <b>visível</b> — CLAUDE.md exige "consentimento visível" para
/// qualquer acesso estilo NDesk. Um Mode=OneWay aplicado a um caminho de binding errado (ex.:
/// "PendingConsent.OperatorNam" por engano) não lança nenhuma exceção: some silenciosamente,
/// sem crash e sem falha de teste — exatamente o tipo de regressão que o critério "não lança"
/// não pega. Este teste lê o texto de fato renderizado na árvore visual (via TextBlock.Inlines)
/// e confere que os dados do pedido de consentimento aparecem tal como o broker os forneceu.
/// </summary>
public sealed class NDeskTabViewConsentContentTests
{
    [Fact]
    public async Task Renders_WithPendingConsentVisible_ShowsConsentDataFromBroker()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var tabViewModel = new NDeskTabViewModel(broker);
        var ticket = await broker.CreateTicketAsync(
            workspaceId: "ws-local",
            operatorDisplayName: "Operador Demo",
            requestedMode: "control",
            permissionsRequested: ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        List<string>? renderedTexts = null;
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var view = new NDeskTabView { DataContext = tabViewModel };
            var window = new Window
            {
                Content = view,
                Width = 400,
                Height = 300,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                renderedTexts = CollectRunTexts(view);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(captured);
        Assert.NotNull(renderedTexts);

        // Cada campo do NDeskConsentRequest precisa estar de fato na árvore renderizada — não
        // basta a ViewModel expor o valor certo, o XAML precisa apontar para o caminho certo.
        Assert.Contains("Operador Demo", renderedTexts);       // OperatorDisplayName
        Assert.Contains("RemoteOps", renderedTexts);            // CompanyName (broker loopback)
        Assert.Contains(ticket.Id, renderedTexts);              // TicketId
        Assert.Contains("control", renderedTexts);              // RequestedMode
        Assert.Contains("view, control", renderedTexts);        // PermissionsRequestedText
    }

    private static List<string> CollectRunTexts(DependencyObject root)
    {
        var texts = new List<string>();
        CollectRunTextsRecursive(root, texts);
        return texts;
    }

    private static void CollectRunTextsRecursive(DependencyObject node, List<string> texts)
    {
        if (node is TextBlock textBlock)
        {
            foreach (Run run in textBlock.Inlines.OfType<Run>())
            {
                if (!string.IsNullOrEmpty(run.Text))
                {
                    texts.Add(run.Text);
                }
            }
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            CollectRunTextsRecursive(VisualTreeHelper.GetChild(node, i), texts);
        }
    }
}
