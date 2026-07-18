using System.Net;
using System.Net.Mail;

namespace RemoteOps.Cloud.Email;

/// <summary>
/// Envia por SMTP (<see cref="System.Net.Mail"/> — sem dependência nova). Config por env do OPERADOR:
/// <c>Smtp:Host</c>, <c>Smtp:Port</c> (587), <c>Smtp:Username</c>, <c>Smtp:Password</c>,
/// <c>Smtp:From</c>, <c>Smtp:UseSsl</c> (true). O servidor NUNCA embute a credencial de SMTP — ela
/// vem do <c>.env</c> do operador (ver <c>.env.example</c>).
/// </summary>
public sealed class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var host = config["Smtp:Host"]
                   ?? throw new InvalidOperationException("Smtp:Host não configurado.");
        var port = int.TryParse(config["Smtp:Port"], out var p) ? p : 587;
        var from = config["Smtp:From"] ?? config["Smtp:Username"] ?? "no-reply@remoteops.local";
        var user = config["Smtp:Username"];
        var pass = config["Smtp:Password"];
        // Default seguro: TLS ligado a menos que explicitamente desligado.
        var useSsl = !bool.TryParse(config["Smtp:UseSsl"], out var s) || s;

        using var client = new SmtpClient(host, port) { EnableSsl = useSsl };
        if (!string.IsNullOrEmpty(user))
            client.Credentials = new NetworkCredential(user, pass);

        using var mail = new MailMessage(from, message.ToEmail, message.Subject, message.TextBody);
        await client.SendMailAsync(mail, ct);
        logger.LogInformation("Recovery email sent to {To} via {Host}:{Port}", message.ToEmail, host, port);
    }
}
