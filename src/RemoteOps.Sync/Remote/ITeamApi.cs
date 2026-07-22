namespace RemoteOps.Sync.Remote;

/// <summary>
/// Endpoints de TIME (Fatia 1). Todos AUTENTICADOS — inclusive os de convite: o convidado já criou
/// a conta dele antes de aceitar, e é o e-mail dessa conta que o convite tem de bater.
///
/// <para>Separado do <see cref="ICloudSyncApi"/> pelo mesmo motivo do <see cref="IMfaApi"/>: ciclo
/// de vida diferente (roda em ações pontuais do operador, não no laço de sync) e superfície
/// diferente. Implementações nunca recebem o CÓDIGO do convite — só o hash e blobs opacos.</para>
/// </summary>
public interface ITeamApi
{
    /// <summary>
    /// Cria um TIME: workspace novo e VAZIO, com a membership Owner de quem chama já carregando o
    /// embrulho da WK. É o que mantém os equipamentos do cofre pessoal fora do alcance do time — sem
    /// ele, o "time" seria o próprio workspace pessoal e o convidado baixaria o acervo inteiro.
    /// </summary>
    /// <exception cref="CloudSyncException">
    /// 409 = o id sorteado já existe (o chamador sorteia outro GUID e repete). Qualquer outro status
    /// também estoura: erro engolido aqui deixaria o app achando que o time existe quando ele não.
    /// </exception>
    Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
        CreateTeamWorkspaceRequest request, CancellationToken ct = default);

    /// <summary>Cria o convite. O blob e o hash já vêm prontos do cliente.</summary>
    Task<CreateTeamInviteResponse> CreateInviteAsync(
        string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default);

    /// <summary>Troca a prova do código pelo blob da WK. Não consome o convite.</summary>
    Task<TeamInviteContextResponse> GetInviteContextAsync(
        string inviteId, string codeHash, CancellationToken ct = default);

    /// <summary>Conclui o aceite gravando a WK re-embrulhada sob a AMK do convidado.</summary>
    Task<AcceptTeamInviteResponse> AcceptInviteAsync(
        string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default);

    /// <summary>
    /// A WK do workspace embrulhada sob a AMK de quem pergunta, ou <c>null</c> quando o servidor
    /// responde 404 — que aqui NÃO é erro: é a resposta "este workspace é pessoal, a chave se
    /// deriva da AMK". Traduzir isso em exceção faria o cliente tratar o cofre pessoal como falha.
    /// </summary>
    Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
        string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Publica o embrulho da chave do time DESTA conta. Idempotente: republicar o mesmo blob é
    /// no-op. É o que faz o dono do time — que nunca aceitou convite nenhum — ter chave guardada no
    /// servidor, e portanto o que faz o segundo computador dele RESTAURAR em vez de sortear outra.
    /// </summary>
    Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
        string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default);

    /// <summary>
    /// Quem está no time. Falha de rede/permissão ESTOURA: uma lista vazia devolvida em silêncio
    /// diria ao operador "você está sozinho no time" — a mentira mais cara que esta tela pode contar.
    /// </summary>
    Task<TeamMembersResponse> GetMembersAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Tira alguém do time. Devolve o desfecho em vez de só "ok": ver <see cref="TeamMemberRemoval"/>.
    /// </summary>
    Task<TeamMemberRemoval> RemoveMemberAsync(
        string workspaceId, string userId, CancellationToken ct = default);
}
