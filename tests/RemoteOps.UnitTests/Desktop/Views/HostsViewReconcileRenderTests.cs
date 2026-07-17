using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;
using RemoteOps.UnitTests.Desktop;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Views;

/// <summary>
/// A reconciliação da Fase 2 muta as <c>ObservableCollection</c> (Groups/Hosts/DeviceFilters) que a
/// <see cref="HostsView"/> BINDA — inclusive o <c>DataGrid</c> com <c>SelectedItem</c> TwoWay. Mutar
/// coleção bindada é a superfície clássica de crash de runtime WPF que passa no build. Este teste
/// renderiza a View de verdade (thread STA + tema real), seleciona um host num DataGrid VIVO, roda a
/// reconciliação com um host novo no store e prova que: (a) não lança; (b) o host novo aparece; (c) a
/// seleção sobrevive (mesma instância continua selecionada). Screenshot não funciona nesta máquina —
/// por isso a verificação é por render STA, não visual.
/// </summary>
public sealed class HostsViewReconcileRenderTests
{
    private static SessionLauncher Launcher() => new(new TabsViewModel(), null, null, null, null, null, null);

    [Fact]
    public async Task Reconcile_Through_Live_DataGrid_PreservesSelection_And_AddsHost_WithoutThrowing()
    {
        var store = new InMemoryLocalStore();

        // Semeia o store fora da thread STA (dado puro, sem WPF) — await aqui satisfaz o analisador.
        var g = await store.AddGroupAsync("ws-local", "Innet");
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r1" });
        await store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r2" });

        var vm = new HostsViewModel(store, Launcher(), "ws-local");
        AssetViewModel? selectedBefore = null;

        Exception? captured = StaThreadRunner.Run(() =>
        {
            var view = new HostsView { DataContext = vm };
            var window = new Window
            {
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                Width = 800,
                Height = 600,
            };
            try
            {
                window.Show();
                window.UpdateLayout();

                // Carrega grupos, abre o grupo e realiza a lista (DataGrid) com layout.
                vm.LoadAsync().GetAwaiter().GetResult();
                vm.OpenGroupCommand.Execute(vm.Groups.Single());
                window.UpdateLayout();

                // Seleciona um host num DataGrid VIVO (binding TwoWay ativo).
                selectedBefore = vm.Hosts.First(h => h.Name == "r1");
                vm.SelectedHost = selectedBefore;
                window.UpdateLayout();

                // Chega um host novo pelo sync → reconciliação sobre as coleções bindadas.
                store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", GroupId = g.Id, Name = "r3" })
                    .GetAwaiter().GetResult();
                vm.ReconcileFromStoreAsync().GetAwaiter().GetResult();
                window.UpdateLayout();
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(captured is null, captured?.ToString());
        Assert.Equal(3, vm.Hosts.Count);
        Assert.Contains(vm.Hosts, h => h.Name == "r3");
        Assert.Same(selectedBefore, vm.SelectedHost); // seleção preservada através do DataGrid vivo
    }
}
