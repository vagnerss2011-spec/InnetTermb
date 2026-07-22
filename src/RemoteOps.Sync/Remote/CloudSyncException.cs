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
        : this(statusCode, reason: null)
    {
    }

    /// <param name="reason">
    /// O motivo em forma de MÁQUINA que o servidor pôs no <c>reason</c> do ProblemDetails (ex.:
    /// <c>workspace.personal</c>). Separado do status de propósito: um status pode um dia servir a
    /// mais de um desfecho, e o cliente que decide o que dizer ao operador precisa de algo estável
    /// que não seja substring da mensagem em pt-BR — essa muda na primeira revisão de texto.
    ///
    /// <para>Nunca carrega segredo: é um código curto, de vocabulário fechado (ADR-013).</para>
    /// </param>
    public CloudSyncException(HttpStatusCode statusCode, string? reason)
        : base($"Cloud sync API retornou status {(int)statusCode} ({statusCode}).")
    {
        StatusCode = statusCode;
        Reason = reason;
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

    /// <summary>
    /// O <c>reason</c> do ProblemDetails, quando o servidor mandou um. <c>null</c> significa
    /// "o servidor não disse" — e nunca deve ser lido como "não é aquele motivo".
    /// </summary>
    public string? Reason { get; }
}

/// <summary>
/// Os motivos que o servidor manda em <c>reason</c> e que o CLIENTE trata de forma própria.
///
/// <para>A duplicata (a definição original é <c>PersonalWorkspaceException.ReasonCode</c>, no
/// assembly da nuvem, que o Desktop não referencia) é amarrada por teste — o mesmo desenho dos
/// <c>TeamRoles</c>. Sem a amarra, renomear o motivo lá deixaria a tela daqui mostrando o recado
/// genérico "o servidor recusou a operação (HTTP 422)", que não diz ao operador nem o que aconteceu
/// nem o que fazer.</para>
/// </summary>
public static class CloudRefusalReasons
{
    /// <summary>Operação de TIME pedida num workspace que é cofre pessoal (HTTP 422).</summary>
    public const string PersonalWorkspace = "workspace.personal";
}
