using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteOps.Desktop.Views;

public partial class HostListView : UserControl
{
    public HostListView()
    {
        InitializeComponent();
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
