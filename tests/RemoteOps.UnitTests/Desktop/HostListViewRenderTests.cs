using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Achado de varredura adversarial (mesma classe do crash de produção coberto por
/// <c>NDeskTabViewRenderTests</c>): <c>DataGridTextColumn.Binding</c> é reaproveitado tanto
/// para gerar o <c>TextBlock</c> de exibição quanto o <c>TextBox</c> de edição da célula, e
/// <c>TextBox.Text</c> tem <c>BindsTwoWayByDefault=true</c>. As colunas de
/// <c>HostListView.xaml</c> apontam para <see cref="AssetViewModel.Name"/>,
/// <see cref="AssetViewModel.PrimaryProtocol"/>, <see cref="AssetViewModel.PrimaryAddress"/>,
/// <see cref="AssetViewModel.Vendor"/> e <see cref="AssetViewModel.Tags"/> — todas
/// somente leitura (expression-bodied, sem setter), igual à <c>PermissionsRequestedText</c>
/// que derrubou o app.
///
/// Hoje isso nunca dispara porque <c>DataGrid.IsReadOnly="True"</c> em
/// <c>HostListView.xaml</c> impede a grid de entrar em modo de edição — o
/// <c>TextBox</c> de edição (e o binding TwoWay que ele carregaria) nunca chega a ser
/// instanciado. Estes testes fixam essa proteção como uma invariante verificável: se algum
/// dia <c>IsReadOnly</c> for removido (ex.: para permitir renomear host inline) sem que as
/// colunas ou as propriedades ganhem proteção equivalente, um duplo-clique numa célula
/// reproduziria a mesma <see cref="InvalidOperationException"/> do bug original — só que
/// fora de <c>App.OnStartup</c>, então cairia no <c>DispatcherUnhandledException</c> de
/// <c>App.xaml.cs</c> em vez de matar o processo silenciosamente.
/// </summary>
public sealed class HostListViewRenderTests
{
    [Fact]
    public void Grid_WithPopulatedAsset_IsReadOnly_OnGridAndOnEveryColumn()
    {
        Exception? captured = StaThreadRunner.Run(() =>
        {
            var store = new InMemoryLocalStore();
            var vm = new HostListViewModel(store, "ws-test");
            vm.NewHostName = "router-core-01";
            vm.AddHostAsync().GetAwaiter().GetResult();

            var view = new HostListView { DataContext = vm };
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

                var grid = FindVisualChild<DataGrid>(view)
                    ?? throw new InvalidOperationException(
                        "DataGrid não encontrado na árvore visual de HostListView — " +
                        "verifique se a estrutura do XAML mudou.");

                Assert.True(grid.Items.Count > 0, "Setup do teste falhou em popular um asset.");

                // Invariante mestra: DataGrid.IsReadOnly=True é o único motivo do binding das
                // colunas (para propriedades sem setter) nunca virar TwoWay de fato.
                Assert.True(
                    grid.IsReadOnly,
                    "HostListView.xaml: DataGrid.IsReadOnly deveria ser True — as colunas fazem " +
                    "bind de propriedades somente leitura de AssetViewModel (Name, Vendor, " +
                    "PrimaryProtocol, PrimaryAddress, Tags). Sem essa trava, editar uma célula " +
                    "lançaria InvalidOperationException ao anexar o TextBox.Text de edição.");

                // O IsReadOnly de uma DataGridColumn é coerced a partir do DataGrid pai: mesmo que
                // nenhuma coluna declare IsReadOnly hoje, confirmamos aqui o valor efetivo pós-coerção
                // — é o que realmente decide se o TextBox de edição é instanciado.
                Assert.All(grid.Columns, column =>
                    Assert.True(
                        column.IsReadOnly,
                        $"Coluna '{column.Header}' não está IsReadOnly (valor coercido) — " +
                        "ela ficaria editável e o binding para uma propriedade sem setter " +
                        "derrubaria o app ao editar."));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Null(captured);
    }

    /// <summary>
    /// Teste negativo complementar, sem WPF: documenta a premissa de que a proteção acima
    /// depende inteiramente das propriedades permanecerem somente leitura. Se alguém adicionar
    /// um setter a uma destas propriedades (ex.: para permitir edição inline), este teste falha
    /// imediatamente apontando para a revisão necessária em HostListView.xaml.
    /// </summary>
    [Theory]
    [InlineData(nameof(AssetViewModel.Name))]
    [InlineData(nameof(AssetViewModel.PrimaryProtocol))]
    [InlineData(nameof(AssetViewModel.PrimaryAddress))]
    [InlineData(nameof(AssetViewModel.Vendor))]
    [InlineData(nameof(AssetViewModel.Tags))]
    public void GridBoundProperty_OnAssetViewModel_HasNoSetter(string propertyName)
    {
        PropertyInfo? property = typeof(AssetViewModel).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.False(
            property!.CanWrite,
            $"AssetViewModel.{propertyName} ganhou um setter. Isso muda a premissa de segurança " +
            "de HostListViewRenderTests.Grid_WithPopulatedAsset_IsReadOnly_OnGridAndOnEveryColumn " +
            "— revise se DataGrid.IsReadOnly ainda é necessário em HostListView.xaml ou se o " +
            "binding da coluna correspondente precisa de Mode=OneWay explícito.");
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }

            T? found = FindVisualChild<T>(child);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}
