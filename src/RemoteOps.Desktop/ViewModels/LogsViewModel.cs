using System.Collections.ObjectModel;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed class LogsViewModel : BaseViewModel, IUiLogSink
{
    public ObservableCollection<string> Events { get; } = [];

    public void Emit(string line)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(() => Events.Insert(0, line));
        else
            Events.Insert(0, line);
    }
}
