namespace RemoteOps.Cloud.Data.Entities;

/// <summary>
/// Convite para entrar num workspace de TIME. Espelha a disciplina do
/// <see cref="PasswordResetTokenEntity"/> — hash + TTL + uso único — e acrescenta a peça que faz o
/// E2EE sobreviver ao compartilhamento: a WK do time viaja aqui EMBRULHADA, e a chave que a abre
/// nunca passa pelo servidor.
///
/// <para>Como o dono monta isto (tudo no cliente): sorteia um código de 160 bits, deriva
/// <c>K_invite = HKDF(código)</c>, embrulha a WK sob essa chave e sobe SÓ o blob
/// (<see cref="WrappedWkByInvite"/>) mais o SHA-256 do código (<see cref="CodeHash"/>). O servidor
/// guarda os dois e não consegue nada com eles: o hash não volta a ser código, e sem o código não
/// existe <c>K_invite</c>. Por isso o código vai por OUTRO canal (WhatsApp/telefone) e o e-mail leva
/// só o link — quem lê o e-mail sozinho não entra no cofre.</para>
/// </summary>
public sealed class InviteEntity
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>E-mail do convidado, já normalizado (<c>EmailNormalizer</c>) — é o destino do convite.</summary>
    public required string Email { get; set; }

    /// <summary>Papel que a membership vai receber no aceite. Validado contra <c>Roles</c> na criação.</summary>
    public required string Role { get; set; }

    /// <summary>
    /// SHA-256 (hex) do código de convite; NUNCA o código. Serve só para o servidor reconhecer quem
    /// já tem o código — a comparação é em tempo constante (ver <c>InviteService</c>).
    /// </summary>
    public required string CodeHash { get; set; }

    /// <summary>
    /// A WK do time embrulhada sob <c>K_invite</c>. Blob OPACO (nonce||tag||ciphertext): o servidor
    /// não tem <c>K_invite</c> e nunca vai ter.
    /// </summary>
    public required byte[] WrappedWkByInvite { get; set; }

    /// <summary>
    /// Versão da WK embrulhada aqui. Viaja para a membership no aceite para que uma rotação futura
    /// deixe rastro: sem versão, um estado misto v1/v2 seria indetectável.
    /// </summary>
    public int WkVersion { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Quando foi aceito. Nulo = ainda vale. Uso ÚNICO, e é token de concorrência (ver AppDbContext).</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>Quem aceitou (id da conta). Nulo enquanto pendente.</summary>
    public Guid? AcceptedByUserId { get; set; }

    /// <summary>Quem convidou — a auditoria de um time precisa saber de quem partiu o acesso.</summary>
    public Guid InvitedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public WorkspaceEntity Workspace { get; set; } = null!;
}
