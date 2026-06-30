using RemoteOps.Contracts.Audit;

namespace RemoteOps.MikroTik;

public interface IWinBoxAuditSink
{
    Task EmitAsync(AuditEvent evt, CancellationToken ct = default);
}
