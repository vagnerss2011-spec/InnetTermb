using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Rdp.Fakes;

internal sealed class InMemoryRdpAuditSink : IRdpAuditSink
{
    private readonly List<RdpAuditEvent> _events = [];

    public IReadOnlyList<RdpAuditEvent> Events => _events;

    public Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default)
    {
        _events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
