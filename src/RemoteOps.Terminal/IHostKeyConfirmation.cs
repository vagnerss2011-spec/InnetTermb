namespace RemoteOps.Terminal;

/// <summary>
/// Apresenta ao usuário uma host key nova ou alterada e aguarda confirmação explícita.
/// <para>
/// A implementação DEVE ser genuinamente assíncrona (ex.: TaskCompletionSource resolvido
/// pela UI) para evitar deadlock quando chamado no contexto de uma conexão SSH — NUNCA
/// usar .GetAwaiter().GetResult() internamente (FIX 1 / ADR-008).
/// </para>
/// </summary>
public interface IHostKeyConfirmation
{
    /// <param name="host">Hostname ou IP do servidor.</param>
    /// <param name="fingerprintHex">Fingerprint SHA-256 em hex sem separadores.</param>
    /// <param name="isChanged">true se uma key anterior existia e foi substituída (mais crítico).</param>
    /// <returns>true = usuário confia; false = usuário rejeita (conexão abortada).</returns>
    Task<bool> ConfirmAsync(string host, string fingerprintHex, bool isChanged, CancellationToken ct = default);
}
