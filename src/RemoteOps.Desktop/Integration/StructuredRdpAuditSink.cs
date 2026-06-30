using System.Diagnostics;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Integration;

internal sealed class StructuredRdpAuditSink : IRdpAuditSink
{
    public Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default)
    {
        var line = auditEvent.CertificateThumbprint is not null
            ? $"[AUDIT][rdp] action={auditEvent.Action} session={auditEvent.SessionId} " +
              $"host={auditEvent.Host} cert={auditEvent.CertificateThumbprint} " +
              $"user={auditEvent.UserId} at={auditEvent.OccurredAt:O}"
            : $"[AUDIT][rdp] action={auditEvent.Action} session={auditEvent.SessionId} " +
              $"host={auditEvent.Host} user={auditEvent.UserId} at={auditEvent.OccurredAt:O}";
        Trace.WriteLine(line);
        return Task.CompletedTask;
    }
}
