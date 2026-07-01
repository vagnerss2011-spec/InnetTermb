using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.NDesk;

/// <summary>Painel mock do lado atendido: mostra o pedido de consentimento e permite aceitar/recusar/encerrar.</summary>
public sealed class NDeskAssistedViewModel : BaseViewModel
{
    private INDeskAgentSession? _session;

    public NDeskAssistedViewModel(INDeskBrokerClient broker)
    {
        broker.IncomingSessionRequested += OnIncomingSessionRequested;

        AcceptCommand = new RelayCommand(
            _ => _ = RespondAsync(true),
            _ => _session?.State == NDeskSessionState.AwaitingConsent);
        DeclineCommand = new RelayCommand(
            _ => _ = RespondAsync(false),
            _ => _session?.State == NDeskSessionState.AwaitingConsent);
        EndCommand = new RelayCommand(
            _ => _ = EndAsync(),
            _ => _session?.State == NDeskSessionState.Connected);
    }

    public NDeskConsentRequest? PendingConsent { get; private set; }

    public string? PermissionsRequestedText =>
        PendingConsent == null ? null : string.Join(", ", PendingConsent.PermissionsRequested);

    public NDeskSessionState State => _session?.State ?? NDeskSessionState.Idle;

    public bool HasPendingRequest => _session?.State == NDeskSessionState.AwaitingConsent;

    public RelayCommand AcceptCommand { get; }

    public RelayCommand DeclineCommand { get; }

    public RelayCommand EndCommand { get; }

    private void OnIncomingSessionRequested(INDeskAgentSession session)
    {
        _session = session;
        PendingConsent = session.ConsentRequest;
        session.StateChanged += OnSessionStateChanged;
        RaiseAll();
    }

    private void OnSessionStateChanged(NDeskSessionState newState)
    {
        if (newState == NDeskSessionState.Ended)
        {
            if (_session != null)
            {
                _session.StateChanged -= OnSessionStateChanged;
                _session = null;
            }

            PendingConsent = null;
        }

        RaiseAll();
    }

    private Task RespondAsync(bool accepted) => _session?.RespondConsentAsync(accepted) ?? Task.CompletedTask;

    private Task EndAsync() => _session?.EndAsync() ?? Task.CompletedTask;

    private void RaiseAll()
    {
        RaisePropertyChanged(nameof(PendingConsent));
        RaisePropertyChanged(nameof(PermissionsRequestedText));
        RaisePropertyChanged(nameof(State));
        RaisePropertyChanged(nameof(HasPendingRequest));
        AcceptCommand.RaiseCanExecuteChanged();
        DeclineCommand.RaiseCanExecuteChanged();
        EndCommand.RaiseCanExecuteChanged();
    }
}
