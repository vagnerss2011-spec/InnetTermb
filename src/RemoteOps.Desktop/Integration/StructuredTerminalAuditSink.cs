using System.Diagnostics;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class StructuredTerminalAuditSink : ITerminalAuditSink
{
    private readonly IUiLogSink? _uiLog;

    public StructuredTerminalAuditSink(IUiLogSink? uiLog = null) => _uiLog = uiLog;

    public Task EmitAsync(TerminalAuditEvent auditEvent, CancellationToken ct = default)
    {
        // Fingerprint (SHA-256) é derivado da chave pública — não é segredo.
        var line = auditEvent.Fingerprint is not null
            ? $"[AUDIT][terminal] action={auditEvent.Action} session={auditEvent.SessionId} " +
              $"host={auditEvent.Host} proto={auditEvent.Protocol} " +
              $"fingerprint={auditEvent.Fingerprint} user={auditEvent.UserId} at={auditEvent.OccurredAt:O}"
            : $"[AUDIT][terminal] action={auditEvent.Action} session={auditEvent.SessionId} " +
              $"host={auditEvent.Host} proto={auditEvent.Protocol} " +
              $"user={auditEvent.UserId} at={auditEvent.OccurredAt:O}";
        Trace.WriteLine(line);

        // Aba Logs (auditoria de acessos visível ao operador; sem segredo por construção).
        _uiLog?.Emit(
            $"{auditEvent.OccurredAt:HH:mm:ss} {auditEvent.Protocol}://{auditEvent.Host} — {auditEvent.Action}" +
            (auditEvent.UserId is { } u ? $" ({u})" : string.Empty));

        return Task.CompletedTask;
    }
}
