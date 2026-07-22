namespace RemoteOps.Sync.Remote;

// Espelhos cliente-side das mensagens de TIME (Fatia 1), no mesmo espírito de AccountContracts e
// SecretsContracts: RemoteOps.Sync não referencia o assembly do servidor; a forma JSON (camelCase,
// blobs em base64) é o contrato, e ele fica fixado pelo TeamContractsWireTests.
//
// A regra que manda aqui é a mesma dos SecretEnvelope: TODO campo binário é base64 de um blob
// OPACO. O servidor não tem a WK, não tem K_invite e não tem o código — ele guarda e devolve bytes.
// O que NÃO existe nestes tipos é a parte mais importante: o CÓDIGO do convite. Ele nasce e morre
// na máquina do dono e do convidado; para o servidor só vai o SHA-256 dele.

/// <summary>
/// <c>POST /workspaces/{id}/invites</c>. O cliente do dono já fez toda a cripto: sorteou o código,
/// derivou <c>K_invite</c>, embrulhou a WK do time sob ela e calculou o hash do código.
/// </summary>
public sealed record CreateTeamInviteRequest(
    string Email,
    string Role,
    string CodeHash,
    string WrappedWkByInvite,
    int WkVersion);

/// <summary>
/// Resposta da criação. <paramref name="EmailDelivered"/> vem <c>false</c> quando o SMTP falhou: o
/// convite VALE do mesmo jeito, mas a tela precisa dizer isso — senão o dono espera um e-mail que
/// nunca vai chegar (falha silenciosa clássica).
/// </summary>
public sealed record CreateTeamInviteResponse(
    string InviteId,
    string Email,
    string Role,
    DateTimeOffset ExpiresAt,
    bool EmailDelivered);

/// <summary>
/// <c>POST /invites/{id}/context</c>: o convidado prova que tem o código (mandando o hash) e recebe
/// a WK embrulhada para abrir LOCALMENTE. São dois passos porque o cliente não tem o que enviar no
/// aceite antes de receber este blob.
/// </summary>
public sealed record TeamInviteContextRequest(string CodeHash);

/// <summary>Convite aberto: o blob para desembrulhar com <c>K_invite</c> + o que a tela mostra.</summary>
public sealed record TeamInviteContextResponse(
    string WorkspaceId,
    string WorkspaceName,
    string Role,
    string WrappedWkByInvite,
    int WkVersion);

/// <summary>
/// <c>POST /invites/{id}/accept</c>: o convidado já abriu a WK e a re-embrulhou sob a PRÓPRIA AMK —
/// sobe só o blob novo. A <c>WkVersion</c> não vai aqui de propósito: quem manda é o convite.
/// </summary>
public sealed record AcceptTeamInviteRequest(string CodeHash, string WrappedWk);

/// <summary>Aceite concluído.</summary>
/// <param name="SessionRefreshRequired">
/// <c>true</c> quando o access token que está na mão do convidado ficou obsoleto: entrar no time de
/// outro tenant faz a conta pertencer a dois, e o claim <c>tenant_id</c> já emitido continua
/// apontando o antigo — todo acesso ao workspace do time volta 403 até o refresh. O servidor avisa
/// para o cliente renovar ANTES de usar o cofre novo, em vez de o operador ver "sem permissão" sem
/// motivo aparente.
/// </param>
public sealed record AcceptTeamInviteResponse(
    string WorkspaceId,
    string WorkspaceName,
    string Role,
    int WkVersion,
    bool SessionRefreshRequired);

/// <summary>
/// <c>GET /workspaces/{id}/key</c>: a WK do time embrulhada sob a AMK de QUEM PERGUNTA. É o que faz
/// o SEGUNDO device do membro abrir o cofre — a AMK é portável, mas o embrulho em disco é local.
/// 404 do servidor significa "este workspace não tem chave de time para você", que é como o cliente
/// distingue cofre de TIME de cofre PESSOAL (raiz AMK, que deriva a chave).
/// </summary>
public sealed record TeamWorkspaceKeyResponse(string WorkspaceId, string WrappedWk, int WkVersion);

/// <summary>
/// <c>GET /workspaces/{id}/members</c> — uma pessoa do time.
///
/// <para>Repare no que NÃO vem: o embrulho da WK. O de cada membro só abre com a AMK DELE, então
/// mandá-lo aos outros não serviria a ninguém e só aumentaria a superfície.</para>
/// </summary>
/// <param name="HasWk">
/// <c>false</c> = a pessoa ainda não tem a chave do time. Ela enxerga a lista do cofre e <b>não abre
/// senha nenhuma</b> — precisa aparecer ESCRITO na tela de Equipe, senão vira "a senha não abre" no
/// meio de um atendimento, sem ninguém ligar uma coisa à outra.
/// </param>
public sealed record TeamMemberDto(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    bool HasWk,
    int WkVersion);

/// <summary>Espelho de <c>TeamMembersResponse</c> do servidor.</summary>
public sealed record TeamMembersResponse(IReadOnlyList<TeamMemberDto> Members);

/// <summary>
/// Desfecho de <c>DELETE /workspaces/{id}/members/{userId}</c>.
///
/// <para><b>Por que enum e não exceção:</b> o servidor tem TRÊS respostas legítimas (removi / essa
/// pessoa não era membro / é o último dono) e as três precisam virar frases DIFERENTES na tela.
/// Espremer as duas últimas num <c>throw</c> genérico faria a tela dizer "não foi possível remover"
/// e engolir exatamente o motivo que o operador precisa saber para agir.</para>
/// </summary>
public enum TeamMemberRemoval
{
    /// <summary>Membership apagada — o acesso está cortado do próximo pedido em diante.</summary>
    Removed,

    /// <summary>Não havia membership (204 aqui seria mentira na tela).</summary>
    NotAMember,

    /// <summary>Último dono: remover deixaria o time sem ninguém que possa administrá-lo.</summary>
    LastOwner,
}
