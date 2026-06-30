using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Audit;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Contracts.NDesk;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Contracts.Sync;
using RemoteOps.MikroTik;
using RemoteOps.Security;
using RemoteOps.Sync;
using RemoteOps.Terminal;
using Xunit;

namespace RemoteOps.UnitTests;

public sealed class SmokeTests
{
    // ── Contracts ─────────────────────────────────────────────────────────────

    [Fact]
    public void Contracts_SessionRequest_CanInstantiate()
    {
        var request = new SessionRequest
        {
            SessionId = "sess-01",
            Protocol = RemoteProtocol.Ssh,
            EndpointId = "ep-01",
            CredentialRefId = "cr-01",
        };

        Assert.Equal("ssh", request.Protocol);
        Assert.True(request.PreferIpv6);
    }

    [Fact]
    public void Contracts_SessionHandle_CanInstantiate()
    {
        var handle = new SessionHandle
        {
            SessionId = "sess-01",
            Protocol = RemoteProtocol.Ssh,
            EndpointId = "ep-01",
            OpenedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IsOpen = true,
        };

        Assert.True(handle.IsOpen);
        Assert.Equal("ssh", handle.Protocol);
    }

    [Fact]
    public void Contracts_Asset_CanInstantiate()
    {
        var asset = new Asset
        {
            Id = "asset-01",
            WorkspaceId = "ws-01",
            Name = "router-borda-01",
        };

        Assert.Equal("router-borda-01", asset.Name);
        Assert.Empty(asset.Tags);
        Assert.Empty(asset.Endpoints);
    }

    [Fact]
    public void Contracts_CredentialRef_NeverExposesSecret()
    {
        var cred = new CredentialRef
        {
            Id = "cr-01",
            Name = "Senha OLTs",
            Type = "password",
            SecretEnvelopeId = "env-01",
        };

        Assert.Null(cred.Metadata);
        Assert.Equal("env-01", cred.SecretEnvelopeId);
    }

    [Fact]
    public void Contracts_AuditEvent_CanInstantiate()
    {
        var evt = new AuditEvent
        {
            Id = "audit-01",
            WorkspaceId = "ws-01",
            ActorUserId = "user-01",
            Action = "session.ssh.opened",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("session.ssh.opened", evt.Action);
        Assert.Empty(evt.Metadata);
    }

    [Fact]
    public void Contracts_SyncChange_CanInstantiate()
    {
        var change = new SyncChange
        {
            EntityType = "asset",
            EntityId = "asset-01",
            Operation = "updated",
            BaseVersion = 3,
            Patch = new Dictionary<string, object?> { ["name"] = "novo-nome" },
        };

        Assert.Equal("updated", change.Operation);
        Assert.Single(change.Patch);
    }

    [Fact]
    public void Contracts_NDeskTicket_CanInstantiate()
    {
        var ticket = new NDeskTicket
        {
            Id = "ticket-01",
            WorkspaceId = "ws-01",
            ExpiresAt = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero),
            Status = "waiting",
        };

        Assert.Equal("waiting", ticket.Status);
        Assert.Empty(ticket.PermissionsRequested);
    }

    [Fact]
    public void Contracts_ExternalToolLaunchRequest_CanInstantiate()
    {
        var req = new ExternalToolLaunchRequest
        {
            Id = "launch-01",
            WorkspaceId = "ws-01",
            Tool = "winbox",
            Target = new ExternalToolTarget { Address = "2001:db8::1", Port = 8291 },
            RequestedBy = "user-01",
            RequestedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal("winbox", req.Tool);
        Assert.False(req.IncludePasswordArgument);
    }

    // ── Module interfaces ──────────────────────────────────────────────────────

    [Fact]
    public void Security_ICredentialVault_IsInterface()
    {
        Assert.True(typeof(ICredentialVault).IsInterface);
    }

    [Fact]
    public void Terminal_ITerminalSessionProvider_IsInterface()
    {
        Assert.True(typeof(ITerminalSessionProvider).IsInterface);
    }

    [Fact]
    public void MikroTik_IMikroTikSessionProvider_IsInterface()
    {
        Assert.True(typeof(IMikroTikSessionProvider).IsInterface);
    }

    [Fact]
    public void MikroTik_IWinBoxRunner_IsInterface()
    {
        Assert.True(typeof(IWinBoxRunner).IsInterface);
    }

    [Fact]
    public void Sync_ISyncClient_IsInterface()
    {
        Assert.True(typeof(ISyncClient).IsInterface);
    }
}
