using RemoteOps.Contracts.NDesk;

namespace RemoteOps.Desktop.NDesk;

/// <summary>
/// Sessão NDesk viva, compartilhada entre o ViewModel do operador e o painel mock do
/// lado atendido. A troca pela implementação real (broker da Frente 3) é uma troca de
/// registro DI — esta interface é o contrato estável.
/// </summary>
public interface INDeskAgentSession
{
    string SessionId { get; }

    NDeskTicket Ticket { get; }

    NDeskConsentRequest ConsentRequest { get; }

    NDeskSessionState State { get; }

    event Action<NDeskSessionState>? StateChanged;

    /// <summary>Chamado pelo lado atendido. Só tem efeito a partir de AwaitingConsent.</summary>
    Task RespondConsentAsync(bool accepted, CancellationToken ct = default);

    /// <summary>Encerra a sessão. Chamável por qualquer lado, em qualquer estado ativo — idempotente.</summary>
    Task EndAsync(CancellationToken ct = default);
}
