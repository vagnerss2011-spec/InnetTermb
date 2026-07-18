namespace RemoteOps.Cloud.Email;

/// <summary>
/// Enviador DEFAULT: loga a mensagem em vez de enviar. É o que faz a recuperação funcionar ponta a
/// ponta antes de o operador configurar o SMTP (dev, CI, primeiro boot).
///
/// Loga em WARNING de propósito, com o corpo inteiro (inclui o token): assim o operador PERCEBE que
/// nenhum email está saindo de verdade e ainda consegue completar/testar o fluxo manualmente pegando
/// o token do log. Em produção, o <c>EmailServiceSetup</c> troca por SMTP e este sender não roda.
/// </summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogWarning(
            "EMAIL NÃO ENVIADO (SMTP não configurado — usando LoggingEmailSender). "
            + "Para: {To} | Assunto: {Subject}\n{Body}",
            message.ToEmail, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}
