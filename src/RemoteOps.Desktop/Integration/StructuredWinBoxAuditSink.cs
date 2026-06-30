using System.Diagnostics;
using RemoteOps.Contracts.Audit;
using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.Integration;

internal sealed class StructuredWinBoxAuditSink : IWinBoxAuditSink
{
    public Task EmitAsync(AuditEvent evt, CancellationToken ct = default)
    {
        Trace.WriteLine(
            $"[AUDIT][winbox] action={evt.Action} workspace={evt.WorkspaceId} " +
            $"actor={evt.ActorUserId} target={evt.TargetId} at={evt.CreatedAt:O}");
        return Task.CompletedTask;
    }
}
