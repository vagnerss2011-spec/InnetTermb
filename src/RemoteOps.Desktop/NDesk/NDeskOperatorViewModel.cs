using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.NDesk;

/// <summary>Painel do operador: gerar/inserir ticket, conectar, ver estado, encerrar.</summary>
public sealed class NDeskOperatorViewModel : BaseViewModel
{
    private readonly INDeskBrokerClient _broker;
    private readonly string _workspaceId;

    private string _ticketIdInput = string.Empty;
    private RemoteOps.Contracts.NDesk.NDeskTicket? _ticket;
    private INDeskAgentSession? _session;
    private string? _errorMessage;

    public NDeskOperatorViewModel(INDeskBrokerClient broker, string workspaceId = "ws-local")
    {
        _broker = broker;
        _workspaceId = workspaceId;

        GenerateTicketCommand = new RelayCommand(_ => _ = GenerateTicketAsync());
        ConnectCommand = new RelayCommand(
            _ => _ = ConnectAsync(),
            _ => !string.IsNullOrWhiteSpace(TicketIdInput) && _session == null);
        EndCommand = new RelayCommand(
            _ => _ = EndAsync(),
            _ => _session != null && State != NDeskSessionState.Ended);
    }

    public string TicketIdInput
    {
        get => _ticketIdInput;
        set
        {
            Set(ref _ticketIdInput, value);
            ConnectCommand.RaiseCanExecuteChanged();
        }
    }

    public RemoteOps.Contracts.NDesk.NDeskTicket? Ticket
    {
        get => _ticket;
        private set => Set(ref _ticket, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public NDeskSessionState State => _session?.State ?? NDeskSessionState.Idle;

    public bool IsSessionActive => State is NDeskSessionState.AwaitingConsent or NDeskSessionState.Connected;

    public RelayCommand GenerateTicketCommand { get; }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand EndCommand { get; }

    private async Task GenerateTicketAsync()
    {
        Ticket = await _broker.CreateTicketAsync(_workspaceId, "Operador Demo", "control", ["view", "control"]);
        TicketIdInput = Ticket.Id;
        ErrorMessage = null;
    }

    private async Task ConnectAsync()
    {
        try
        {
            _session = await _broker.ConnectAsync(TicketIdInput.Trim());
            _session.StateChanged += OnSessionStateChanged;
            ErrorMessage = null;
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }

        RaiseState();
        ConnectCommand.RaiseCanExecuteChanged();
        EndCommand.RaiseCanExecuteChanged();
    }

    private async Task EndAsync()
    {
        if (_session == null) return;
        await _session.EndAsync();
    }

    private void OnSessionStateChanged(NDeskSessionState _)
    {
        RaiseState();
        EndCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
    }

    private void RaiseState()
    {
        RaisePropertyChanged(nameof(State));
        RaisePropertyChanged(nameof(IsSessionActive));
    }
}
