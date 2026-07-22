using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using RemoteOps.Cloud.Sync;
using RemoteOps.Cloud.Teams;

namespace RemoteOps.Cloud.Errors;

public sealed class CloudExceptionHandler(ILogger<CloudExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx,
        Exception exception,
        CancellationToken ct)
    {
        var correlationId = ctx.Items.TryGetValue("X-Correlation-Id", out var cid)
            ? cid?.ToString()
            : ctx.TraceIdentifier;

        var (status, title) = exception switch
        {
            RbacDeniedException => (403, "Acesso negado"),
            // 422, e não 403 nem 409: o pedido está bem formado e quem pede TEM a permissão — o que
            // não existe é o objeto da operação (um time). 403 diria "você não pode" e mandaria o
            // operador atrás de permissão que ele já tem; 409 já significa "embrulho divergente" no
            // PUT /key, e o cliente reage a ele indo buscar a chave guardada — um caminho que aqui
            // só produziria um segundo recado errado. Status novo nesta API = nenhum desfecho antigo
            // é reinterpretado.
            PersonalWorkspaceException => (422, "Operação de time num cofre pessoal"),
            // Query/body malformado é culpa do cliente: sem isto o binding do minimal
            // API (ex.: parâmetro obrigatório ausente) virava 500 e escondia o motivo.
            BadHttpRequestException => (400, "Requisição inválida"),
            ArgumentException or FormatException => (400, "Requisição inválida"),
            _ => (500, "Erro interno"),
        };

        if (status == 500)
            logger.LogError(exception, "Unhandled exception correlationId={CorrelationId}", correlationId);
        else
            logger.LogWarning("Handled exception {Type} correlationId={CorrelationId}: {Message}",
                exception.GetType().Name, correlationId, exception.Message);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status < 500 ? exception.Message : "Contate o suporte com o correlationId.",
            Extensions = { ["correlationId"] = correlationId },
        };

        // Motivo em forma de MÁQUINA, ao lado da frase para o humano. O texto do detail é pt-BR e
        // pode ser reescrito; o cliente precisa de algo estável para decidir o que fazer, e sem isto
        // ele teria de casar substring de mensagem — que quebra na primeira revisão de texto.
        if (exception is PersonalWorkspaceException)
        {
            problem.Extensions["reason"] = PersonalWorkspaceException.ReasonCode;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
