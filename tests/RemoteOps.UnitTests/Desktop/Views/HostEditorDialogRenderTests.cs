using System.Windows;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// Regressão de crash de produção: clicar "Novo host" (ou "Editar") lançava XamlParseException
/// dentro de InitializeComponent() de <see cref="HostEditorDialog"/> — o Window declarava
/// ResizeMode="CanResizeWithGrips", que não é um valor válido do enum ResizeMode (o correto é
/// CanResizeWithGrip, no singular). O valor de enum em XAML é uma string convertida em runtime,
/// então o build (com TreatWarningsAsErrors) passava e o erro só aparecia ao abrir o diálogo.
/// App.OnDispatcherUnhandledException capturava como "Erro inesperado" e o editor nunca abria,
/// impedindo o operador de cadastrar/editar qualquer host (regressão desde v1.1.2). Renderiza o
/// diálogo de verdade (thread STA + tema real) para provar que instancia e faz layout sem lançar.
/// </summary>
public sealed class HostEditorDialogRenderTests
{
    [Fact]
    public void Constructs_ForNewHost_WithoutThrowing()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostEditorViewModel(store, "ws-local", existing: null, groupId: null);

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var dialog = new HostEditorDialog(vm)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
            };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
    }
}
