using System.Security.Claims;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Hubs;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class SyncHubTests
{
    // OnConnectedAsync roda a CADA conexão nova — e reconexão gera ConnectionId novo. É o único
    // ponto onde dá pra garantir o grupo sem depender de o cliente lembrar de re-entrar.
    [Fact]
    public async Task OnConnected_AutoJoins_All_User_Workspaces()
    {
        var userId = Guid.NewGuid();
        var ws1 = Guid.NewGuid();
        var ws2 = Guid.NewGuid();
        using var db = CreateDb();
        db.Memberships.Add(Membership(ws1, userId));
        db.Memberships.Add(Membership(ws2, userId));
        db.Memberships.Add(Membership(Guid.NewGuid(), Guid.NewGuid()));
        await db.SaveChangesAsync();

        var groups = new RecordingGroupManager();
        using var hub = new SyncHub(db)
        {
            Context = new FakeHubCallerContext(userId, "conn-1"),
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.Equal(2, groups.Added.Count);
        Assert.Contains(("conn-1", ws1.ToString()), groups.Added);
        Assert.Contains(("conn-1", ws2.ToString()), groups.Added);
    }

    // Grupo do SignalR é string case-sensitive; o broadcast usa ToString() ("D" minúsculo).
    // Entrar com o GUID cru em maiúsculas colocaria o cliente num grupo que nunca recebe nada.
    [Fact]
    public async Task JoinWorkspace_Uses_Canonical_Group_Name()
    {
        var userId = Guid.NewGuid();
        var wsId = Guid.NewGuid();
        using var db = CreateDb();
        db.Memberships.Add(Membership(wsId, userId));
        await db.SaveChangesAsync();

        var groups = new RecordingGroupManager();
        using var hub = new SyncHub(db)
        {
            Context = new FakeHubCallerContext(userId, "conn-1"),
            Groups = groups,
        };

        await hub.JoinWorkspace(wsId.ToString().ToUpperInvariant());

        Assert.Equal([("conn-1", wsId.ToString())], groups.Added);
    }

    // Sair com outra grafia deixaria o cliente dentro do grupo, recebendo hint de um workspace
    // que ele acabou de abandonar.
    [Fact]
    public async Task LeaveWorkspace_Uses_Canonical_Group_Name()
    {
        var wsId = Guid.NewGuid();
        using var db = CreateDb();

        var groups = new RecordingGroupManager();
        using var hub = new SyncHub(db)
        {
            Context = new FakeHubCallerContext(Guid.NewGuid(), "conn-1"),
            Groups = groups,
        };

        await hub.LeaveWorkspace(wsId.ToString().ToUpperInvariant());

        Assert.Equal([("conn-1", wsId.ToString())], groups.Removed);
    }

    [Fact]
    public async Task JoinWorkspace_NonMember_Does_Not_Join()
    {
        using var db = CreateDb();

        var groups = new RecordingGroupManager();
        using var hub = new SyncHub(db)
        {
            Context = new FakeHubCallerContext(Guid.NewGuid(), "conn-1"),
            Groups = groups,
        };

        await hub.JoinWorkspace(Guid.NewGuid().ToString());

        Assert.Empty(groups.Added);
    }

    // Sem "sub" válido não dá pra saber de quem são as memberships: entrar em grupo nenhum é a
    // única saída segura (vazaria hint de workspace alheio).
    [Fact]
    public async Task OnConnected_Without_Subject_Joins_Nothing()
    {
        using var db = CreateDb();
        db.Memberships.Add(Membership(Guid.NewGuid(), Guid.NewGuid()));
        await db.SaveChangesAsync();

        var groups = new RecordingGroupManager();
        using var hub = new SyncHub(db)
        {
            Context = new FakeHubCallerContext(userId: null, "conn-1"),
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.Empty(groups.Added);
    }

    private static AppDbContext CreateDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"synchub-{Guid.NewGuid()}")
            .Options);

    private static MembershipEntity Membership(Guid workspaceId, Guid userId)
        => new() { WorkspaceId = workspaceId, UserId = userId, Role = "Operator" };
}

/// <summary>
/// IGroupManager que só anota as chamadas. O repo não usa biblioteca de mock — o que importa aqui
/// é o par (ConnectionId, nome do grupo), e a grafia do grupo é justamente o que se quer provar.
/// </summary>
internal sealed class RecordingGroupManager : IGroupManager
{
    public List<(string ConnectionId, string GroupName)> Added { get; } = [];

    public List<(string ConnectionId, string GroupName)> Removed { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Removed.Add((connectionId, groupName));
        return Task.CompletedTask;
    }
}

/// <summary>
/// HubCallerContext mínimo: o hub só lê ConnectionId e a claim "sub". <c>userId</c> nulo simula
/// conexão sem subject utilizável.
/// </summary>
internal sealed class FakeHubCallerContext(Guid? userId, string connectionId) : HubCallerContext
{
    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity(
        userId is null ? [] : [new Claim("sub", userId.Value.ToString())],
        "test"));

    public override string ConnectionId => connectionId;

    public override string? UserIdentifier => userId?.ToString();

    public override ClaimsPrincipal? User => _user;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
}
