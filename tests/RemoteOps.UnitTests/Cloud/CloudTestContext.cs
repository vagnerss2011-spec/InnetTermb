using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Hubs;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Contexto de teste compartilhado: AppDbContext InMemory + serviços reais.
/// Cada instância usa um banco isolado por nome único.
/// </summary>
internal sealed class CloudTestContext : IDisposable
{
    public AppDbContext Db { get; }
    public PermissionEvaluator Rbac { get; }
    public AuditService Audit { get; }
    public SyncService Sync { get; }

    private static int _counter;

    public CloudTestContext()
    {
        var dbName = $"remoteops-test-{Interlocked.Increment(ref _counter)}";
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Db = new AppDbContext(opts);
        Rbac = new PermissionEvaluator(Db);
        Audit = new AuditService(Db, NullLogger<AuditService>.Instance);

        var nullHub = new NullHubContext();
        Sync = new SyncService(Db, Rbac, Audit, nullHub, NullLogger<SyncService>.Instance);
    }

    // ── Helpers de seed ────────────────────────────────────────────────────

    public async Task<(TenantEntity Tenant, WorkspaceEntity Workspace, UserEntity User, MembershipEntity Membership)>
        SeedActiveUserAsync(string role = "Operator")
    {
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Tenant Test",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var workspace = new WorkspaceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "WS Test",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = $"user-{Guid.NewGuid()}@test.local",
            DisplayName = "Test User",
            Status = "active",
            PasswordHash = "v1:test:test",
            MfaRequired = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var membership = new MembershipEntity
        {
            WorkspaceId = workspace.Id,
            UserId = user.Id,
            Role = role,
        };

        Db.Tenants.Add(tenant);
        Db.Workspaces.Add(workspace);
        Db.Users.Add(user);
        Db.Memberships.Add(membership);
        await Db.SaveChangesAsync();

        return (tenant, workspace, user, membership);
    }

    public void Dispose() => Db.Dispose();
}

/// <summary>Fake IHubContext que não faz nada — suficiente para testes unitários de SyncService.</summary>
internal sealed class NullHubContext : IHubContext<SyncHub>
{
    public IHubClients Clients => NullHubClients.Instance;
    public IGroupManager Groups => NullGroupManager.Instance;
}

internal sealed class NullHubClients : IHubClients
{
    public static readonly NullHubClients Instance = new();
    public IClientProxy All => NullClientProxy.Instance;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;
    public IClientProxy Client(string connectionId) => NullClientProxy.Instance;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NullClientProxy.Instance;
    public IClientProxy Group(string groupName) => NullClientProxy.Instance;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => NullClientProxy.Instance;
    public IClientProxy User(string userId) => NullClientProxy.Instance;
    public IClientProxy Users(IReadOnlyList<string> userIds) => NullClientProxy.Instance;
}

internal sealed class NullClientProxy : IClientProxy
{
    public static readonly NullClientProxy Instance = new();
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class NullGroupManager : IGroupManager
{
    public static readonly NullGroupManager Instance = new();
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
