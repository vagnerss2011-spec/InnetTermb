using System.Diagnostics;
using RemoteOps.Contracts.Audit;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.Integration;

internal sealed class StructuredWinBoxAuditSink : IWinBoxAuditSink
{
    private readonly IUiLogSink? _uiLog;

    public StructuredWinBoxAuditSink(IUiLogSink? uiLog = null) => _uiLog = uiLog;

    public Task EmitAsync(AuditEvent evt, CancellationToken ct = default)
    {
        Trace.WriteLine(
            $"[AUDIT][winbox] action={evt.Action} workspace={evt.WorkspaceId} " +
            $"actor={evt.ActorUserId} target={evt.TargetId} at={evt.CreatedAt:O}");

        // Aba Logs (Metadata do AuditEvent nunca contém segredo por contrato).
        _uiLog?.Emit(
            $"{evt.CreatedAt:HH:mm:ss} winbox {evt.TargetId ?? evt.TargetType ?? string.Empty} — {evt.Action} ({evt.ActorUserId})");

        return Task.CompletedTask;
    }
}
