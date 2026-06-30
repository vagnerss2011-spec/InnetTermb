using System.Diagnostics;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class StructuredTerminalAuditSink : ITerminalAuditSink
{
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
        return Task.CompletedTask;
    }
}
