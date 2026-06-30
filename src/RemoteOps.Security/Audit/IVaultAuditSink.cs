namespace RemoteOps.Security.Audit;

/// <summary>Destino dos eventos de auditoria do cofre.</summary>
public interface IVaultAuditSink
{
    Task EmitAsync(VaultAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>Sink nulo (descarta eventos). Padrão quando nenhum sink é injetado.</summary>
public sealed class NullVaultAuditSink : IVaultAuditSink
{
    public static NullVaultAuditSink Instance { get; } = new();

    private NullVaultAuditSink()
    {
    }

    public Task EmitAsync(VaultAuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
}
