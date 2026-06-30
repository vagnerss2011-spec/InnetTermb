namespace RemoteOps.Terminal;

/// <summary>Destino dos eventos de auditoria de sessões terminal (SSH/Telnet).</summary>
public interface ITerminalAuditSink
{
    Task EmitAsync(TerminalAuditEvent auditEvent, CancellationToken ct = default);
}
