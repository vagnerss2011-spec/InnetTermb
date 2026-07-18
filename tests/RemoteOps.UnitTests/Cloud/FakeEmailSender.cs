using RemoteOps.Cloud.Email;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// <see cref="IEmailSender"/> de teste: captura as mensagens em memória em vez de enviar. Os testes
/// do PasswordResetService o usam para pescar o token do "email" e provar o fluxo de reset ponta a
/// ponta sem SMTP.
/// </summary>
internal sealed class FakeEmailSender : IEmailSender
{
    private readonly object _gate = new();
    private readonly List<EmailMessage> _sent = [];

    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_gate) { return _sent.ToList(); } }
    }

    public EmailMessage? Last
    {
        get { lock (_gate) { return _sent.Count > 0 ? _sent[^1] : null; } }
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        lock (_gate) { _sent.Add(message); }
        return Task.CompletedTask;
    }
}
