using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RemoteOps.MikroTik.Audit;

/// <summary>
/// Writes structured audit events to ILogger. No secrets ever reach this sink.
/// </summary>
public sealed class ConsoleWinBoxAuditSink(ILogger<ConsoleWinBoxAuditSink> logger) : IWinBoxAuditSink
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public Task RecordAsync(WinBoxAuditEvent auditEvent, CancellationToken ct = default)
    {
        logger.LogInformation("[WINBOX-AUDIT] {EventType} req={RequestId} host={HostId} login={Login} target={ConnectTo} ipv6={IsIPv6} romon={IsRoMon} pwdArg={PwdArg} pid={Pid} err={Error}",
            auditEvent.Type,
            auditEvent.RequestId,
            auditEvent.HostId,
            auditEvent.Login,
            auditEvent.ConnectTo,
            auditEvent.IsIPv6,
            auditEvent.IsRoMon,
            auditEvent.PasswordArgumentUsed,
            auditEvent.Pid?.ToString() ?? "-",
            auditEvent.Error ?? "-");

        return Task.CompletedTask;
    }
}
