using System.Windows;
using RemoteOps.Desktop.NDesk;
using RemoteOps.UnitTests.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.NDesk;

/// <summary>
/// Renderiza NDeskTabView de verdade (thread STA + Window real) em vez de só testar a
/// ViewModel. Regressão de um crash de produção: um `&lt;Run Text="{Binding ...}"&gt;` sem
/// Mode=OneWay herda BindsTwoWayByDefault=true do WPF; se a propriedade for somente leitura,
/// o binding lança InvalidOperationException assim que a árvore visual é layoutada — e como
/// isso acontece dentro de App.OnStartup (antes do dispatcher bombear mensagens), nada
/// captura a exceção e o processo morre sem diálogo, mesmo com REMOTEOPS_FEATURE_FLAGS
/// só ligando ndesk.enabled e nenhuma sessão sendo aberta ainda (o painel some do layout
/// via Visibility=Collapsed, mas o WPF ainda anexa o binding).
/// </summary>
public sealed class NDeskTabViewRenderTests
{
    [Fact]
    public void Renders_WithNoPendingConsent_WithoutThrowing()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var broker = new LoopbackNDeskBrokerClient();
            var tabViewModel = new NDeskTabViewModel(broker);
            RenderAndLayout(tabViewModel);
        });

        Assert.Null(captured);
    }

    [Fact]
    public async Task Renders_WithPendingConsentVisible_WithoutThrowing()
    {
        var broker = new LoopbackNDeskBrokerClient();
        var tabViewModel = new NDeskTabViewModel(broker);
        var ticket = await broker.CreateTicketAsync(
            "ws-local", "Operador Demo", "control", ["view", "control"]);
        await broker.ConnectAsync(ticket.Id);

        Exception? captured = StaThreadRunner.Run(() => RenderAndLayout(tabViewModel));

        Assert.Null(captured);
    }

    private static void RenderAndLayout(NDeskTabViewModel tabViewModel)
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
        }
        finally
        {
            window.Close();
        }
    }
}
