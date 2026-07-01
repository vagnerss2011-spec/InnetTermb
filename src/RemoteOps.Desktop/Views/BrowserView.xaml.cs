using System.Windows;
using System.Windows.Controls;

namespace RemoteOps.Desktop.Views;

public partial class BrowserView : UserControl
{
    public BrowserView()
    {
        InitializeComponent();
    }

    // Avatar da conta: abre o ContextMenu (Configurações / Verificar atualizações / Sobre)
    // como dropdown, mesmo padrão do botão "⌄" em HostsView.
    private void AvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
