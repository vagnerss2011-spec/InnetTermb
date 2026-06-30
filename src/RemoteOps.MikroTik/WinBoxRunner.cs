using System.Diagnostics;
using RemoteOps.MikroTik.Audit;
using RemoteOps.MikroTik.Models;

namespace RemoteOps.MikroTik;

public sealed record WinBoxLaunchResult(
    bool Success,
    int? Pid,
    string? Error,
    bool PasswordArgumentUsed
);

public sealed class WinBoxRunner(
    string executablePath,
    WinBoxToolManifest manifest,
    IWinBoxPolicyProvider policy,
    IWinBoxAuditSink audit)
{
    public async Task<WinBoxLaunchResult> LaunchAsync(
        WinBoxLaunchRequest request,
        string? password = null,
        CancellationToken ct = default)
    {
        var connectTo = WinBoxArgumentBuilder.BuildConnectTo(request.Target);
        var isIPv6 = request.Target.IsIPv6Literal;
        var isRoMon = request.RoMon is { Enabled: true };

        await audit.RecordAsync(new WinBoxAuditEvent(
            WinBoxAuditEventType.OpenRequested,
            request.Id, request.WorkspaceId, request.HostId,
            request.Login, connectTo, DateTimeOffset.UtcNow,
            IsIPv6: isIPv6, IsRoMon: isRoMon), ct);

        var hashResult = manifest.ValidateExecutable(executablePath);
        if (!hashResult.IsValid)
        {
            await audit.RecordAsync(new WinBoxAuditEvent(
                WinBoxAuditEventType.OpenFailed,
                request.Id, request.WorkspaceId, request.HostId,
                request.Login, connectTo, DateTimeOffset.UtcNow,
                Error: hashResult.Error), ct);

            return new WinBoxLaunchResult(false, null, hashResult.Error, false);
        }

        await audit.RecordAsync(new WinBoxAuditEvent(
            WinBoxAuditEventType.ToolValidated,
            request.Id, request.WorkspaceId, request.HostId,
            request.Login, connectTo, DateTimeOffset.UtcNow,
            ManifestVersion: hashResult.MatchedVersion), ct);

        var policyDecision = await policy.EvaluateAsync(request, ct);
        if (!policyDecision.Allowed)
        {
            await audit.RecordAsync(new WinBoxAuditEvent(
                WinBoxAuditEventType.OpenFailed,
                request.Id, request.WorkspaceId, request.HostId,
                request.Login, connectTo, DateTimeOffset.UtcNow,
                Error: policyDecision.DenyReason), ct);

            return new WinBoxLaunchResult(false, null, policyDecision.DenyReason, false);
        }

        var allowPassword = policyDecision.AllowPasswordArgument;

        var psi = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        WinBoxArgumentBuilder.PopulateArgumentList(psi.ArgumentList, request, allowPassword, password);

        int? pid = null;
        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            pid = proc.Id;
        }
        catch (Exception ex)
        {
            await audit.RecordAsync(new WinBoxAuditEvent(
                WinBoxAuditEventType.OpenFailed,
                request.Id, request.WorkspaceId, request.HostId,
                request.Login, connectTo, DateTimeOffset.UtcNow,
                Error: ex.Message, IsIPv6: isIPv6, IsRoMon: isRoMon), ct);

            return new WinBoxLaunchResult(false, null, ex.Message, false);
        }

        if (allowPassword && !string.IsNullOrEmpty(password))
            await audit.RecordAsync(new WinBoxAuditEvent(
                WinBoxAuditEventType.PasswordArgumentUsed,
                request.Id, request.WorkspaceId, request.HostId,
                request.Login, connectTo, DateTimeOffset.UtcNow,
                PasswordArgumentUsed: true), ct);

        if (isIPv6)
            await audit.RecordAsync(new WinBoxAuditEvent(
                WinBoxAuditEventType.IPv6TargetUsed,
                request.Id, request.WorkspaceId, request.HostId,
                request.Login, connectTo, DateTimeOffset.UtcNow,
                IsIPv6: true), ct);

        if (isRoMon)
            await audit.RecordAsync(new WinBoxAuditEvent(
                WinBoxAuditEventType.RoMonUsed,
                request.Id, request.WorkspaceId, request.HostId,
                request.Login, connectTo, DateTimeOffset.UtcNow,
                IsRoMon: true), ct);

        await audit.RecordAsync(new WinBoxAuditEvent(
            WinBoxAuditEventType.OpenStarted,
            request.Id, request.WorkspaceId, request.HostId,
            request.Login, connectTo, DateTimeOffset.UtcNow,
            Pid: pid, IsIPv6: isIPv6, IsRoMon: isRoMon,
            PasswordArgumentUsed: allowPassword && !string.IsNullOrEmpty(password)), ct);

        return new WinBoxLaunchResult(true, pid, null, allowPassword && !string.IsNullOrEmpty(password));
    }
}
