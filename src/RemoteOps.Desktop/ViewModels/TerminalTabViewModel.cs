using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOps.Contracts.Models;
using RemoteOps.Terminal.Core;

namespace RemoteOps.Desktop.ViewModels;

public sealed partial class TerminalTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TerminalSessionManager _manager;
    private TerminalSession? _session;

    [ObservableProperty] private string _header = "Terminal";
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string? _errorMessage;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public TerminalTabViewModel(TerminalSessionManager manager)
    {
        _manager = manager;
    }

    public async Task OpenSessionAsync(SessionRequest request)
    {
        IsConnecting = true;
        ErrorMessage = null;
        Header = $"{request.Protocol.ToString().ToUpperInvariant()} — {request.Host}";

        try
        {
            _session = await _manager.OpenSessionAsync(request);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public TerminalSession? Session => _session;

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (_session is not null)
            await _manager.CloseSessionAsync(SessionId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _manager.CloseSessionAsync(SessionId);
    }
}
