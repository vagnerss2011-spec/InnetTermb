using System.Net;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Falha HTTP do backend de sync (status não esperado). A mensagem contém apenas o status —
/// nunca token, header de autorização ou corpo da resposta (no-secret-in-log, ADR-013).
/// </summary>
public sealed class CloudSyncException : Exception
{
    public CloudSyncException(HttpStatusCode statusCode)
        : base($"Cloud sync API retornou status {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
