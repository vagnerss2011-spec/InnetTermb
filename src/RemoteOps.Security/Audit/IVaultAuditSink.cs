namespace RemoteOps.Security.Audit;

/// <summary>Destino dos eventos de auditoria do cofre.</summary>
public interface IVaultAuditSink
{
    Task EmitAsync(VaultAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>
/// Sink nulo (descarta eventos). NÃO é default: o <see cref="CredentialVault"/> exige
/// um sink explícito (ADR-003 — "recuperação exige auditoria"). Use esta instância
/// apenas quando descartar eventos for uma decisão consciente (ex.: teste isolado).
/// </summary>
public sealed class NullVaultAuditSink : IVaultAuditSink
{
    public static NullVaultAuditSink Instance { get; } = new();

    private NullVaultAuditSink()
    {
    }

    public Task EmitAsync(VaultAuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
}
