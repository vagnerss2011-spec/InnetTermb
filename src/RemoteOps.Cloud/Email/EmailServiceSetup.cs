namespace RemoteOps.Cloud.Email;

/// <summary>
/// Escolhe o <see cref="IEmailSender"/>: <see cref="SmtpEmailSender"/> quando <c>Smtp:Host</c> está
/// configurado; senão <see cref="LoggingEmailSender"/> (dev/CI/primeiro boot — a recuperação roda
/// ponta a ponta sem SMTP, com o token indo pro log). O servidor nunca embute credencial de SMTP.
/// </summary>
public static class EmailServiceSetup
{
    public static IServiceCollection AddEmailSender(this IServiceCollection services, IConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config["Smtp:Host"]))
            services.AddScoped<IEmailSender, LoggingEmailSender>();
        else
            services.AddScoped<IEmailSender, SmtpEmailSender>();

        return services;
    }

    /// <summary>true se o SMTP está configurado (para o startup logar qual enviador está ativo).</summary>
    public static bool SmtpConfigured(IConfiguration config) => !string.IsNullOrWhiteSpace(config["Smtp:Host"]);
}
