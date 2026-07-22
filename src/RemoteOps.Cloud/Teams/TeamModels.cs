namespace RemoteOps.Cloud.Teams;

// Mensagens do time no fio. Vale a mesma regra dos SecretEnvelope: TODO campo binário é base64 de um
// blob OPACO — o servidor não tem a WK, não tem K_invite e não tenta interpretar nada.

/// <summary>
/// <c>POST /workspaces/{id}/invites</c>. O dono já fez todo o trabalho de cripto no cliente: sorteou
/// o código, derivou <c>K_invite</c>, embrulhou a WK e calculou o SHA-256 do código.
///
/// <para>Repare no que NÃO existe aqui: o código. Ele nunca sai da máquina do dono a não ser pelo
/// canal fora-de-banda que o próprio dono escolher.</para>
/// </summary>
public sealed record CreateInviteRequest(
    string Email,
    string Role,
    string CodeHash,
    string WrappedWkByInvite,
    int WkVersion);

/// <summary>
/// Resposta da criação. Devolve o que o dono precisa para montar o convite na tela — e nada mais:
/// nem o código (o servidor nunca o teve) nem o hash dele.
/// </summary>
/// <param name="EmailDelivered">
/// <c>false</c> quando o envio falhou. O convite VALE do mesmo jeito: a tela avisa e o dono repassa
/// link e código na mão. Silenciar isto deixaria o dono esperando um e-mail que nunca chegou.
/// </param>
public sealed record CreateInviteResponse(
    string InviteId,
    string Email,
    string Role,
    DateTimeOffset ExpiresAt,
    bool EmailDelivered);

/// <summary>
/// <c>POST /invites/{id}/context</c>: o convidado prova que tem o código (mandando o SHA-256 dele) e
/// recebe a WK embrulhada para abrir LOCALMENTE. O servidor não participa da abertura.
/// </summary>
public sealed record InviteContextRequest(string CodeHash);

/// <summary>Convite aberto: o blob para desembrulhar com <c>K_invite</c> + o que a tela precisa dizer.</summary>
public sealed record InviteContextResponse(
    string WorkspaceId,
    string WorkspaceName,
    string Role,
    string WrappedWkByInvite,
    int WkVersion);

/// <summary>
/// <c>POST /invites/{id}/accept</c>: o convidado já desembrulhou a WK e a re-embrulhou sob a PRÓPRIA
/// AMK — sobe só o blob novo. O <c>WkVersion</c> NÃO vem daqui: quem manda é o convite, senão o
/// cliente poderia declarar uma versão que não é a da chave que ele guardou.
/// </summary>
public sealed record AcceptInviteRequest(string CodeHash, string WrappedWk);

/// <summary>Aceite concluído: o workspace que a conta acabou de ganhar.</summary>
/// <param name="SessionRefreshRequired">
/// <c>true</c> quando o access token que o convidado tem NA MÃO ficou obsoleto e ele precisa chamar
/// <c>/auth/refresh</c> ANTES de usar o workspace novo.
///
/// <para>Por que isso existe: o token carrega o claim <c>tenant_id</c>, que o
/// <c>PermissionEvaluator</c> usa como guarda cross-tenant. Cada conta nasce no
/// <c>/auth/register</c> com um tenant só dela, então entrar no time do colega significa passar a
/// pertencer a DOIS tenants — e o <c>TokenService</c>, nesse caso, para de emitir o claim (guarda
/// inerte). Só que o token JÁ EMITIDO continua dizendo "tenant B", e todo pull/push/members no
/// workspace do time volta 403 até ele expirar. O aceite funcionaria e o cofre do time ficaria
/// inacessível "sem motivo" — falha muda clássica. Aqui o servidor DIZ que precisa renovar.</para>
/// </param>
public sealed record AcceptInviteResponse(
    string WorkspaceId,
    string WorkspaceName,
    string Role,
    int WkVersion,
    bool SessionRefreshRequired);

/// <summary>
/// Um membro do time. Sem blob: o embrulho de um membro só serve para ELE (está sob a AMK dele).
/// <paramref name="HasWk"/> existe para a tela mostrar quem ainda não tem a chave — um membro sem WK
/// enxerga a lista do cofre e não abre nada, e isso precisa aparecer em vez de virar erro mudo.
/// </summary>
public sealed record TeamMember(
    string UserId,
    string Email,
    string DisplayName,
    string Role,
    bool HasWk,
    int WkVersion);

public sealed record TeamMembersResponse(IReadOnlyList<TeamMember> Members);

/// <summary>
/// <c>GET /workspaces/{id}/key</c>: a WK do time embrulhada sob a AMK de QUEM PERGUNTA. É o que faz
/// o segundo device do membro abrir o cofre — a AMK é portável, mas o blob guardado em disco é local.
/// </summary>
public sealed record WorkspaceKeyResponse(string WorkspaceId, string WrappedWk, int WkVersion);
