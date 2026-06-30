namespace RemoteOps.MikroTik.Audit;

public enum WinBoxAuditEventType
{
    ToolValidated,
    OpenRequested,
    OpenStarted,
    OpenFailed,
    PasswordArgumentUsed,
    IPv6TargetUsed,
    IPv4FallbackUsed,
    RoMonUsed,
}

/// <summary>
/// Immutable audit event. Must never contain passwords, tokens or private keys.
/// </summary>
public sealed record WinBoxAuditEvent(
    WinBoxAuditEventType Type,
    string RequestId,
    string WorkspaceId,
    string HostId,
    string Login,
    string ConnectTo,
    DateTimeOffset OccurredAt,
    int? Pid = null,
    string? Error = null,
    bool PasswordArgumentUsed = false,
    bool IsIPv6 = false,
    bool IsRoMon = false,
    string? ManifestVersion = null
);

public interface IWinBoxAuditSink
{
    Task RecordAsync(WinBoxAuditEvent auditEvent, CancellationToken ct = default);
}
