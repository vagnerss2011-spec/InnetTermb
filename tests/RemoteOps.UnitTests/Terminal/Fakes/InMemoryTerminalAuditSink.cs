using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class InMemoryTerminalAuditSink : ITerminalAuditSink
{
    private readonly List<TerminalAuditEvent> _events = [];

    public IReadOnlyList<TerminalAuditEvent> Events => _events;

    public void Clear() => _events.Clear();

    public Task EmitAsync(TerminalAuditEvent auditEvent, CancellationToken ct = default)
    {
        _events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
