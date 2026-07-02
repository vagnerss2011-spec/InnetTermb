using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class HostsView : UserControl
{
    public HostsView()
    {
        InitializeComponent();
    }

    // Botão "⌄" ao lado de "Novo host": abre o ContextMenu (com "Novo grupo") como dropdown.
    private void NewMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    // Duplo-clique na linha conecta pelo protocolo primário do host.
    private void HostRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: AssetViewModel asset }
            && DataContext is HostsViewModel vm
            && vm.ConnectPrimaryCommand.CanExecute(asset))
        {
            vm.ConnectPrimaryCommand.Execute(asset);
        }
    }

    // Clique-direito não muda a seleção por padrão no DataGrid; sem isto o menu de
    // contexto agiria sobre o host errado (o selecionado antes, não o clicado).
    private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }
}
