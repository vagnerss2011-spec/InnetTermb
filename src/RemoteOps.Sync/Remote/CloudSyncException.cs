using System.Net;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Falha do backend de sync: um status HTTP inesperado, ou um contrato de fio que não bate com o que
/// este cliente sabe falar. A mensagem contém apenas status/campo estrutural — nunca token, header
/// de autorização, corpo da resposta ou material de envelope (no-secret-in-log, ADR-013).
/// </summary>
public sealed class CloudSyncException : Exception
{
    public CloudSyncException(HttpStatusCode statusCode)
        : base($"Cloud sync API retornou status {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Falha de CONTRATO (não de HTTP): campo fora do formato acordado com o backend. Sem
    /// <see cref="StatusCode"/> — não houve resposta ruim, houve dado que não cabe no contrato.
    /// </summary>
    public CloudSyncException(string message)
        : base(message)
    {
    }

    /// <summary>Status HTTP quando a falha veio do servidor; <c>null</c> em falha de contrato.</summary>
    public HttpStatusCode? StatusCode { get; }
}
