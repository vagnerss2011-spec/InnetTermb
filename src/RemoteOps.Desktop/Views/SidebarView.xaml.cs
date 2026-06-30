using System.Windows;
using System.Windows.Controls;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    // TreeView.SelectedItem não é uma DependencyProperty bindável diretamente.
    // Usamos o evento para propagar a seleção ao ViewModel.
    private void OnGroupSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is SidebarViewModel vm)
        {
            vm.SelectedGroup = e.NewValue as AssetGroupViewModel;
        }
    }
}
