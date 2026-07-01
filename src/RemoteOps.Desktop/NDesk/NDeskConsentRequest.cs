namespace RemoteOps.Desktop.NDesk;

/// <summary>Dados exibidos na tela de consentimento do lado atendido (docs/09 §Consentimento e UX).</summary>
public sealed record NDeskConsentRequest(
    string TicketId,
    string OperatorDisplayName,
    string CompanyName,
    string RequestedMode,
    IReadOnlyList<string> PermissionsRequested);
