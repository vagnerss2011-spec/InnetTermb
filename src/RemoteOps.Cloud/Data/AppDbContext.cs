using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data.Entities;

namespace RemoteOps.Cloud.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();
    public DbSet<AssetGroupEntity> AssetGroups => Set<AssetGroupEntity>();
    public DbSet<AssetEntity> Assets => Set<AssetEntity>();
    public DbSet<EndpointEntity> Endpoints => Set<EndpointEntity>();
    public DbSet<CredentialRefEntity> CredentialRefs => Set<CredentialRefEntity>();
    public DbSet<SecretEnvelopeEntity> SecretEnvelopes => Set<SecretEnvelopeEntity>();
    public DbSet<ChangelogEntryEntity> Changelog => Set<ChangelogEntryEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<PasswordResetTokenEntity> PasswordResetTokens => Set<PasswordResetTokenEntity>();
    public DbSet<InviteEntity> Invites => Set<InviteEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
        });

        model.Entity<WorkspaceEntity>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();

            // ── A marca de nascimento (pessoal × time) ──
            // O DEFAULT decide o destino de toda linha que já existe: sem valor gravado, o Postgres
            // preenche `personal`. É o lado seguro E o lado correto — até esta versão o único
            // caminho que criava workspace era o /auth/register, então todo workspace existente é
            // mesmo o cofre pessoal de alguém. Classificar um deles como "time" por engano
            // autorizaria convite no acervo do operador; o inverso só produz uma recusa explicada.
            e.Property(x => x.Kind)
                .HasMaxLength(20)
                .IsRequired()
                .HasDefaultValue(WorkspaceKinds.Personal);

            e.HasIndex(x => x.TenantId);
            e.HasOne(x => x.Tenant).WithMany(x => x.Workspaces).HasForeignKey(x => x.TenantId);
        });

        model.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            // Opcional desde o E2EE: conta nova autentica por AuthHash e não tem senha legada.
            e.Property(x => x.PasswordHash).HasMaxLength(256);
            e.Property(x => x.AuthHashHash).HasMaxLength(256);
        });

        model.Entity<MembershipEntity>(e =>
        {
            e.ToTable("memberships");
            e.HasKey(x => new { x.WorkspaceId, x.UserId });
            e.Property(x => x.Role).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.WorkspaceId);
            e.HasOne(x => x.Workspace).WithMany(x => x.Memberships).HasForeignKey(x => x.WorkspaceId);
            e.HasOne(x => x.User).WithMany(x => x.Memberships).HasForeignKey(x => x.UserId);
        });

        model.Entity<InviteEntity>(e =>
        {
            e.ToTable("invites");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.Role).HasMaxLength(100).IsRequired();
            // 64 hex de SHA-256. O tamanho fixo é guarda de formato: código cru (base32 do
            // RecoveryKeyCodec) não cabe aqui, então um cliente que mandasse o CÓDIGO no lugar do
            // hash quebraria alto em vez de gravar o segredo do time em claro no banco.
            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            // Achar convite pendente do mesmo e-mail (supersessão na criação) sem varrer a tabela.
            e.HasIndex(x => new { x.WorkspaceId, x.Email });
            e.HasOne(x => x.Workspace).WithMany().HasForeignKey(x => x.WorkspaceId);

            // ── Uso único sob corrida ──
            // O aceite lê o convite pendente e só depois grava. Entre a leitura e a gravação, um
            // SEGUNDO aceite passa pela mesma porta: as duas requisições ainda enxergam AcceptedAt
            // nulo, e só o banco pode desempatar. Como token de concorrência, o EF condiciona a
            // UPDATE ao estado lido (`WHERE "AcceptedAt" IS NULL`) e quem perde recebe
            // DbUpdateConcurrencyException — que o InviteService traduz na MESMA recusa genérica.
            //
            // MEDIDO, e não presumido: hoje quem barra a segunda MEMBERSHIP é a chave primária
            // (workspace+usuário) — desligar este token não faz o teste da corrida ficar vermelho,
            // porque o convite é amarrado a UM e-mail e, portanto, a UMA conta. O token protege
            // outra coisa: a linha do CONVITE, que sem ele poderia ser remarcada por quem perdeu a
            // corrida (AcceptedAt/AcceptedByUserId apontando para o aceite que não valeu, num
            // provider sem transação por SaveChanges). Guardado por teste dedicado
            // (Convite_TemTokenDeConcorrencia_NoAcceptedAt) para não sumir num refactor.
            e.Property(x => x.AcceptedAt).IsConcurrencyToken();
        });

        model.Entity<AssetGroupEntity>(e =>
        {
            e.ToTable("asset_groups");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasOne(x => x.Workspace).WithMany(x => x.AssetGroups).HasForeignKey(x => x.WorkspaceId);
        });

        model.Entity<AssetEntity>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.GroupId);
            e.HasOne(x => x.Group).WithMany(x => x.Assets).HasForeignKey(x => x.GroupId);
        });

        model.Entity<EndpointEntity>(e =>
        {
            e.ToTable("endpoints");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AssetId);
            e.Property(x => x.Protocol).HasMaxLength(50).IsRequired();
            e.HasOne(x => x.Asset).WithMany(x => x.Endpoints).HasForeignKey(x => x.AssetId);
        });

        model.Entity<CredentialRefEntity>(e =>
        {
            e.ToTable("credential_refs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(x => x.CredentialRefs).HasForeignKey(x => x.WorkspaceId);
            e.HasOne(x => x.SecretEnvelope).WithMany().HasForeignKey(x => x.SecretEnvelopeId).IsRequired(false);
        });

        model.Entity<SecretEnvelopeEntity>(e =>
        {
            e.ToTable("secret_envelopes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.Property(x => x.Algorithm).HasMaxLength(100).IsRequired();
            e.Property(x => x.KeyVersion).HasMaxLength(100).IsRequired();
            // Cursor único por workspace: uma corrida entre dois upserts vira violação
            // de unicidade (→ 409, cliente re-tenta) em vez de dois envelopes com o
            // mesmo cursor, caso em que um pull `> since` perderia um deles em silêncio.
            e.HasIndex(x => new { x.WorkspaceId, x.Cursor }).IsUnique();

            // ── Concorrência otimista do upsert (pré-requisito de segurança dos times) ──
            //
            // O upsert lê `existing` e só depois grava. Entre a leitura e a gravação, OUTRA
            // requisição pode commitar a lápide de revogação — e o EF, sem token, grava por cima:
            // o material vivo volta a existir sob um envelope já revogado, com Version e Cursor
            // novos, e o servidor propaga isso para todos os devices. Um device que não entende
            // `RevokedAt` grava o envelope como VIVO. Enquanto cada conta deriva a própria WDK o
            // estrago é limitado (o material plantado não abre no PC da vítima); com a chave de
            // workspace COMPARTILHADA do time ele abre — vira ressurreição de senha revogada.
            //
            // Marcar as duas colunas como token faz o EF condicionar a UPDATE ao estado que foi
            // lido (`WHERE "RevokedAt" IS NULL AND "Version" = <lido>`): quem perdeu a corrida
            // recebe DbUpdateConcurrencyException → 409 → o outbox do cliente re-tenta em cima do
            // estado novo. Escolhidas colunas que JÁ EXISTEM (em vez de `xmin`, do Npgsql) porque
            // o provider dos testes é o InMemory, que também enforça token de concorrência: assim
            // a corrida é coberta de verdade em CI, e não só em produção.
            e.Property(x => x.RevokedAt).IsConcurrencyToken();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        model.Entity<ChangelogEntryEntity>(e =>
        {
            e.ToTable("changelog");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.EntityId);
            e.HasIndex(x => new { x.WorkspaceId, x.Id });
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Operation).HasMaxLength(20).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(x => x.Changelog).HasForeignKey(x => x.WorkspaceId);
        });

        model.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            e.Property(x => x.Action).HasMaxLength(200).IsRequired();
            e.Property(x => x.MetadataJson).IsRequired();
            e.HasOne(x => x.Workspace).WithMany(x => x.AuditEvents).HasForeignKey(x => x.WorkspaceId);
        });

        model.Entity<DeviceEntity>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasMaxLength(50).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.Devices).HasForeignKey(x => x.UserId);
        });

        model.Entity<RefreshTokenEntity>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.TokenHash).HasMaxLength(256).IsRequired();
            e.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId);
        });

        model.Entity<PasswordResetTokenEntity>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.TokenHash).HasMaxLength(256).IsRequired();
            // Sem navegação inversa em UserEntity: os tokens de reset são efêmeros e
            // consultados sempre pelo hash, nunca varridos por usuário na aplicação.
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });
    }
}
