using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Auth;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Hubs;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Secrets;
using RemoteOps.Cloud.Sync;
using RemoteOps.Cloud.Teams;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Logger que guarda as mensagens formatadas. Existe para provar AUSÊNCIA: nenhum código de
/// convite, hash ou blob pode aparecer no que o servidor loga (ADR-013).
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get { lock (_gate) { return _messages.ToList(); } }
    }

    /// <summary>Tudo o que foi logado, concatenado — conveniente para um único Assert.DoesNotContain.</summary>
    public string AllText => string.Join("\n", Messages);

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate) { _messages.Add(formatter(state, exception)); }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

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
    public TokenService Tokens { get; }
    public AccountService Accounts { get; }
    public SecretsService Secrets { get; }
    public MfaService Mfa { get; }
    public MfaSecretProtector MfaProtector { get; }

    /// <summary>Enviador fake: os testes de reset pescam o token do "email" capturado aqui.</summary>
    public FakeEmailSender Email { get; }

    /// <summary>PasswordResetService com relógio real (testes de expiração/cooldown criam o seu).</summary>
    public PasswordResetService PasswordReset { get; }

    /// <summary>Convites do time (relógio real; o teste de expiração cria o seu com relógio fixo).</summary>
    public InviteService Invites { get; }

    /// <summary>Membros do time (listar, remover, chave do próprio membro).</summary>
    public TeamService Team { get; }

    /// <summary>Captura o que o InviteService logou — a prova de que código/hash não vazam (ADR-013).</summary>
    public CapturingLogger<InviteService> InviteLogger { get; } = new();

    /// <summary>Relógio fixo compartilhado por MfaService e pelas sessões TOTP dos testes.</summary>
    public static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly IConfiguration _config;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    private static int _counter;

    public static IConfiguration TestConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = "remoteops-test-signing-key-32bytes!!",
                ["Jwt:Issuer"] = "remoteops-test",
                ["Jwt:Audience"] = "remoteops-test",
                ["Auth:KdfDecoyKeyBase64"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

    public CloudTestContext()
    {
        var dbName = $"remoteops-test-{Interlocked.Increment(ref _counter)}";
        // Root explícito: as opções ficam guardadas para que NewDbContext() abra um SEGUNDO
        // contexto sobre o MESMO armazenamento. Sem isso não dá para encenar corrida — cada
        // requisição real tem o próprio DbContext (e o próprio rastreador de mudanças).
        var root = new InMemoryDatabaseRoot();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;

        Db = new AppDbContext(_dbOptions);
        Rbac = new PermissionEvaluator(Db);
        Audit = new AuditService(Db, NullLogger<AuditService>.Instance);

        var nullHub = new NullHubContext();
        Sync = new SyncService(Db, Rbac, Audit, nullHub, NullLogger<SyncService>.Instance);

        _config = TestConfig();
        MfaProtector = new MfaSecretProtector(_config);
        Tokens = new TokenService(Db, _config, MfaProtector, NullLogger<TokenService>.Instance);
        Accounts = new AccountService(Db, Tokens, _config, NullLogger<AccountService>.Instance);
        Secrets = new SecretsService(Db, Rbac, Audit, NullLogger<SecretsService>.Instance);
        Mfa = new MfaService(Db, MfaProtector, NullLogger<MfaService>.Instance) { UtcNow = () => FixedNow };
        Email = new FakeEmailSender();
        PasswordReset = new PasswordResetService(Db, Email, NullLogger<PasswordResetService>.Instance);
        Invites = new InviteService(Db, Rbac, Audit, Email, _config, InviteLogger);
        Team = new TeamService(Db, Rbac, Audit, NullLogger<TeamService>.Instance);
    }

    /// <summary>
    /// <see cref="InviteService"/> com relógio controlável, sobre o MESMO banco. Usado pelos testes
    /// de expiração (TTL de 7 dias não pode virar Task.Delay).
    /// </summary>
    public InviteService InvitesAt(Func<DateTimeOffset> clock) =>
        new(Db, Rbac, Audit, Email, _config, InviteLogger) { UtcNow = clock };

    /// <summary>
    /// <see cref="InviteService"/> amarrado a um contexto específico — o "outro request" da corrida
    /// de aceite concorrente (cada requisição HTTP real tem o próprio DbContext).
    /// </summary>
    public InviteService InvitesOn(AppDbContext other) =>
        new(other,
            new PermissionEvaluator(other),
            new AuditService(other, NullLogger<AuditService>.Instance),
            Email,
            _config,
            InviteLogger);

    /// <summary>TokenService com o relógio fixo — para validar TOTP determinístico no login dos testes.</summary>
    public TokenService TokensAtFixedNow()
        => new(Db, _config, MfaProtector, NullLogger<TokenService>.Instance) { UtcNow = () => FixedNow };

    /// <summary>
    /// Segundo <see cref="AppDbContext"/> sobre o MESMO banco, com rastreador próprio.
    /// Em produção cada requisição HTTP tem o seu contexto — é exatamente essa separação que
    /// permite a corrida "leio vivo aqui, o outro commita a lápide lá, eu gravo por cima".
    /// </summary>
    public AppDbContext NewDbContext() => new(_dbOptions);

    /// <summary>SecretsService amarrado a um contexto específico (o "outro request" da corrida).</summary>
    public static SecretsService SecretsOn(AppDbContext db) => new(
        db,
        new PermissionEvaluator(db),
        new AuditService(db, NullLogger<AuditService>.Instance),
        NullLogger<SecretsService>.Instance);

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

    /// <summary>
    /// Conta ativa SEM membership — é o convidado antes de aceitar. Separado do
    /// <see cref="SeedActiveUserAsync"/> de propósito: o convite existe justamente para a conta que
    /// ainda não pertence ao workspace.
    /// </summary>
    public async Task<UserEntity> SeedAccountAsync(string email)
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = email.Split('@')[0],
            Status = "active",
            MfaRequired = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();
        return user;
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
