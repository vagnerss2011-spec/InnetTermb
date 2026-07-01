using System.Windows.Controls;

namespace RemoteOps.Desktop.NDesk;

/// <summary>
/// Camada fina de binding — sem lógica própria (toda a lógica está nos ViewModels).
/// Não testável em headless; verificação por clique manual documentada no PR.
/// </summary>
public partial class NDeskTabView : UserControl
{
    public NDeskTabView()
    {
        InitializeComponent();
    }
}
