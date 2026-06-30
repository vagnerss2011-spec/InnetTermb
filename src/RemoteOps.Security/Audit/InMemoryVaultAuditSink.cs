using System.Collections.Concurrent;

namespace RemoteOps.Security.Audit;

/// <summary>Sink em memória para testes e inspeção. Thread-safe.</summary>
public sealed class InMemoryVaultAuditSink : IVaultAuditSink
{
    private readonly ConcurrentQueue<VaultAuditEvent> _events = new();

    public IReadOnlyCollection<VaultAuditEvent> Events => _events.ToArray();

    public Task EmitAsync(VaultAuditEvent auditEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        _events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }
}
