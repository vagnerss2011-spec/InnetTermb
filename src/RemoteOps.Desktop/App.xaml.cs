using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteOps.Desktop.Integration;

namespace RemoteOps.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _serviceProvider = AppCompositionRoot.Build();
        var viewModel = _serviceProvider.GetRequiredService<ViewModels.MainViewModel>();
        var window = new MainWindow(viewModel);
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
