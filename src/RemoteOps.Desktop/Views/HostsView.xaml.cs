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

    // O grid de grupos é um ItemsControl: não tem seleção, e o clique esquerdo no card ABRE o grupo.
    // Sem fixar o alvo aqui, o "Excluir grupo" do menu de contexto agiria sobre o alvo anterior (ou
    // ficaria desabilitado para sempre) — mesmo motivo do Row_PreviewMouseRightButtonDown da lista de
    // hosts. Roda ANTES do menu abrir, então o CanExecute do comando já avalia com o alvo certo.
    private void GroupCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GroupCardViewModel card }
            && DataContext is HostsViewModel vm)
        {
            vm.SelectedGroup = card;
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
