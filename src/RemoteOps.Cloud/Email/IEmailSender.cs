namespace RemoteOps.Cloud.Email;

/// <summary>
/// Envio de email plugável. O default (<see cref="LoggingEmailSender"/>) faz o fluxo de recuperação
/// rodar ponta a ponta SEM SMTP (dev, CI, primeiro boot); o operador pluga <c>SmtpEmailSender</c> —
/// ou qualquer outro provedor — só configurando (o servidor nunca embute credencial de SMTP).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
