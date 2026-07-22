namespace RemoteOps.Cloud.Data.Entities;

public sealed class MembershipEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }
    public required string Role { get; set; }

    /// <summary>JSON com permissões granulares sobrescritas para este membro.</summary>
    public string? PermissionsJson { get; set; }

    /// <summary>
    /// A WK do time embrulhada sob a AMK DESTE membro (blob opaco: nonce||tag||ciphertext). O
    /// convidado desembrulha a WK com o código do convite e a re-embrulha sob a própria AMK antes de
    /// subir — por isso cada membro tem um blob DIFERENTE para a MESMA chave, e o servidor não abre
    /// nenhum deles.
    ///
    /// <para>NULO em toda membership de cofre PESSOAL (raiz AMK, que deriva a chave em vez de
    /// guardá-la). Campo aditivo: nada que existe hoje muda de comportamento por causa dele.</para>
    /// </summary>
    public byte[]? WrappedWk { get; set; }

    /// <summary>
    /// Versão da WK embrulhada em <see cref="WrappedWk"/>. <c>0</c> = membership sem WK (cofre
    /// pessoal). Existe DESDE O DIA 1, antes de haver rotação: sem ela, quando a rotação chegar, um
    /// estado misto (um membro na v1, outro na v2) é INDETECTÁVEL — vira erro mudo no PC do colega,
    /// que é exatamente a classe de falha mais cara desta base.
    /// </summary>
    public int WkVersion { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
    public UserEntity User { get; set; } = null!;
}
