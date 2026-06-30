using System.Windows;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ILocalStore store = new InMemoryLocalStore();
        var viewModel = new MainViewModel(store);
        var window = new MainWindow(viewModel);
        window.Show();
    }
}
