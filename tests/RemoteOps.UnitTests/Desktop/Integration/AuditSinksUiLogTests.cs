using System.Linq;
using RemoteOps.Contracts.Audit;
using RemoteOps.Desktop.Integration;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Terminal;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

public sealed class AuditSinksUiLogTests
{
    [Fact]
    public async Task TerminalSink_EmitsReadableLine_ToLogsTab()
    {
        var logs = new LogsViewModel();
        var sink = new StructuredTerminalAuditSink(logs);

        await sink.EmitAsync(new TerminalAuditEvent
        {
            Action = "SessionOpened",
            SessionId = "s1",
            Host = "10.0.0.1",
            Protocol = "ssh",
            UserId = "local-user",
            OccurredAt = DateTimeOffset.Now,
        });

        string line = Assert.Single(logs.Events);
        Assert.Contains("ssh", line);
        Assert.Contains("10.0.0.1", line);
        Assert.Contains("SessionOpened", line);
    }

    [Fact]
    public async Task WinBoxSink_EmitsReadableLine_ToLogsTab()
    {
        var logs = new LogsViewModel();
        var sink = new StructuredWinBoxAuditSink(logs);

        await sink.EmitAsync(new AuditEvent
        {
            Id = "e1",
            WorkspaceId = "ws-local",
            ActorUserId = "local-user",
            Action = "winbox_open_requested",
            TargetId = "10.0.0.2",
            CreatedAt = DateTimeOffset.Now,
        });

        string line = Assert.Single(logs.Events);
        Assert.Contains("winbox", line);
        Assert.Contains("10.0.0.2", line);
    }

    [Fact]
    public async Task Sinks_WithoutUiLog_StillWork()
    {
        var terminal = new StructuredTerminalAuditSink();
        var winbox = new StructuredWinBoxAuditSink();
        await terminal.EmitAsync(new TerminalAuditEvent
        {
            Action = "SessionClosed",
            SessionId = "s1",
            Host = "h",
            Protocol = "ssh",
            OccurredAt = DateTimeOffset.Now,
        });
        await winbox.EmitAsync(new AuditEvent
        {
            Id = "e2",
            WorkspaceId = "ws-local",
            ActorUserId = "u",
            Action = "winbox_open_started",
            CreatedAt = DateTimeOffset.Now,
        });
        // Sem IUiLogSink: só Trace — nada a lançar.
    }
}
