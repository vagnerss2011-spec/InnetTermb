namespace RemoteOps.Terminal;

/// <summary>
/// Solicita consentimento explícito antes de abrir uma sessão Telnet.
/// <para>
/// Telnet transmite tudo em texto puro; o consentimento DEVE bloquear a abertura da
/// conexão TCP até ack explícito do usuário. A implementação usa TaskCompletionSource
/// resolvido pela UI — NUNCA retorna imediatamente sem mostrar o aviso (FIX 2 / ADR-008).
/// </para>
/// <para>Telnet é desabilitado por padrão; ativado apenas para grupos autorizados.</para>
/// </summary>
public interface ITelnetConsentProvider
{
    /// <returns>true = usuário consentiu; false = usuário recusou (conexão abortada).</returns>
    Task<bool> RequestConsentAsync(string host, int port, CancellationToken ct = default);
}
