using System.Security.Cryptography;
using RemoteOps.MikroTik;
using RemoteOps.MikroTik.Audit;
using RemoteOps.MikroTik.Models;
using Xunit;

namespace RemoteOps.MikroTik.Tests;

public sealed class WinBoxRunnerTests
{
    private readonly List<WinBoxAuditEvent> _auditEvents = new();
    private readonly IWinBoxAuditSink _audit;

    public WinBoxRunnerTests()
    {
        _audit = new CapturingAuditSink(_auditEvents);
    }

    [Fact]
    public async Task Launch_ExecutableNotFound_ReturnsFailed_AuditsFailure()
    {
        var manifest = FakeManifest("/nonexistent/winbox64.exe");
        var runner = BuildRunner("/nonexistent/winbox64.exe", manifest);

        var result = await runner.LaunchAsync(BuildRequest());

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_auditEvents, e => e.Type == WinBoxAuditEventType.OpenFailed);
    }

    [Fact]
    public async Task Launch_HashMismatch_ReturnsFailed_AuditsFailure()
    {
        var (tmpExe, cleanup) = CreateTempExe();
        try
        {
            var manifest = FakeManifest(tmpExe, sha256Override: "deadbeef");
            var runner = BuildRunner(tmpExe, manifest);

            var result = await runner.LaunchAsync(BuildRequest());

            Assert.False(result.Success);
            Assert.Contains(_auditEvents, e => e.Type == WinBoxAuditEventType.OpenFailed);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task Launch_AuditEvents_NeverContainPassword()
    {
        var (tmpExe, cleanup) = CreateTempExe();
        try
        {
            var manifest = FakeManifest(tmpExe);
            var runner = BuildRunner(tmpExe, manifest);

            await runner.LaunchAsync(BuildRequest(), password: "supersecret");

            foreach (var ev in _auditEvents)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(ev);
                Assert.DoesNotContain("supersecret", json);
            }
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task Launch_IPv6Target_AuditsIPv6Event()
    {
        var (tmpExe, cleanup) = CreateTempExe();
        try
        {
            var manifest = FakeManifest(tmpExe);
            var runner = BuildRunner(tmpExe, manifest);
            var request = BuildRequest() with
            {
                Target = new WinBoxTarget("2001:db8::1", WinBoxAddressFamily.IPv6, 8291)
            };

            await runner.LaunchAsync(request);

            Assert.Contains(_auditEvents, e => e.Type == WinBoxAuditEventType.IPv6TargetUsed);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task Launch_PasswordPolicySuppressed_DoesNotAuditPasswordUsed()
    {
        var (tmpExe, cleanup) = CreateTempExe();
        try
        {
            var manifest = FakeManifest(tmpExe);
            var policyDeniesPassword = new LocalWinBoxPolicyProvider(globalAllowPasswordArgument: false);
            var runner = BuildRunner(tmpExe, manifest, policyDeniesPassword);

            await runner.LaunchAsync(BuildRequest(includePassword: true), password: "secret");

            Assert.DoesNotContain(_auditEvents, e => e.Type == WinBoxAuditEventType.PasswordArgumentUsed);
        }
        finally { cleanup(); }
    }

    private WinBoxRunner BuildRunner(
        string exePath,
        WinBoxToolManifest manifest,
        IWinBoxPolicyProvider? policy = null) =>
        new(exePath, manifest, policy ?? new LocalWinBoxPolicyProvider(), _audit);

    private static WinBoxLaunchRequest BuildRequest(bool includePassword = false) =>
        new("req-1", "ws-1", "host-1",
            new WinBoxTarget("192.168.88.1", WinBoxAddressFamily.IPv4, 8291),
            "admin", null, includePassword, null, null,
            "user@example.com", DateTimeOffset.UtcNow);

    private static WinBoxToolManifest FakeManifest(string exePath, string? sha256Override = null)
    {
        var actualHash = File.Exists(exePath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exePath))).ToLowerInvariant()
            : "0000000000000000000000000000000000000000000000000000000000000000";

        return new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "test",
            Vendor = "MikroTik",
            File = Path.GetFileName(exePath),
            Sha256 = sha256Override ?? actualHash,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = "test",
        };
    }

    private static (string path, Action cleanup) CreateTempExe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"winbox64_test_{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A]); // MZ stub
        return (path, () => File.Delete(path));
    }

    private sealed class CapturingAuditSink(List<WinBoxAuditEvent> events) : IWinBoxAuditSink
    {
        public Task RecordAsync(WinBoxAuditEvent auditEvent, CancellationToken ct = default)
        {
            events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
