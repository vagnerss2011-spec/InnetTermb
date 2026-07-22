namespace RemoteOps.Cloud.Data.Entities;

public sealed class WorkspaceEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public string? EncryptionPolicy { get; set; }
    public required string Status { get; set; }

    /// <summary>
    /// Como este workspace nasceu: <see cref="WorkspaceKinds.Personal"/> (junto com a conta, no
    /// <c>/auth/register</c>) ou <see cref="WorkspaceKinds.Team"/> (<c>POST /workspaces</c>). É o que
    /// deixa o servidor recusar convite e publicação de chave de time num cofre pessoal sem depender
    /// de o cliente pedir a coisa certa.
    ///
    /// <para><b>Default PESSOAL, e o default aqui decide o destino do que já existe.</b> Toda linha
    /// gravada antes desta coluna existir recebe <see cref="WorkspaceKinds.Personal"/> — inclusive o
    /// workspace do operador, com os ~700 clientes dele. Além de ser o lado seguro (classificar um
    /// pessoal como time abriria o vazamento; o contrário só produz uma recusa explicada), aqui é
    /// também o lado CORRETO: até esta versão, o único caminho que criava workspace era o registro
    /// da conta, então todo workspace existente é, de fato, pessoal.</para>
    ///
    /// <para><b>Por que não há backfill por heurística</b> (ex.: "membership com <c>WrappedWk</c> ⇒
    /// é time"): era exatamente o botão de convite defeituoso que gravava a chave de time na
    /// membership do workspace PESSOAL. Uma heurística assim promoveria a time justamente o cofre
    /// que a marca existe para proteger. Constante, e só constante.</para>
    /// </summary>
    public string Kind { get; set; } = WorkspaceKinds.Personal;

    public DateTimeOffset CreatedAt { get; set; }

    public TenantEntity Tenant { get; set; } = null!;
    public ICollection<MembershipEntity> Memberships { get; set; } = [];
    public ICollection<AssetGroupEntity> AssetGroups { get; set; } = [];
    public ICollection<CredentialRefEntity> CredentialRefs { get; set; } = [];
    public ICollection<ChangelogEntryEntity> Changelog { get; set; } = [];
    public ICollection<AuditEventEntity> AuditEvents { get; set; } = [];
}
