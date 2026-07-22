using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>O cofre do TIME abre neste computador?</summary>
internal enum TeamVaultReadiness
{
    /// <summary>Esta sessão não é de time — não há cofre compartilhado a reparar.</summary>
    NotTeamSession,

    /// <summary>A chave do time está aqui (já estava, ou acabou de chegar agora).</summary>
    KeyHere,

    /// <summary>Segue sem chave: toda operação de senha do time recusa alto (fail-closed).</summary>
    StillWithoutKey,
}

/// <summary>
/// ⚠️ <b>O reparo da chave do time, no boot.</b> Duas metades, e cada uma conserta um estrago
/// diferente:
///
/// <list type="number">
///   <item><b>BUSCAR</b> (sessão <c>TeamWithoutKey</c>): esta máquina abriu o cofre de um time e
///   <b>não tem a chave</b>. É o estado mais incômodo do app — os equipamentos aparecem e nenhuma
///   senha do time abre. Até esta correção o boot só sabia PUBLICAR, e publicar sai antes de tocar a
///   rede quando não há chave local: ou seja, <b>nada</b> no boot tentava buscar. O aviso na tela
///   mandava "conecte-se à internet", e conectar não curava coisa alguma. Agora o boot pede à conta o
///   embrulho guardado (<c>GET /workspaces/{id}/key</c>) e, quando ele chega, o cofre do time passa a
///   abrir <b>nesta mesma sessão</b> — o chaveiro do time é o MESMO objeto que o cofre consulta.</item>
///
///   <item><b>PUBLICAR</b> (sessão de time <b>com</b> a chave): o reparo do 1e′. Quem criou o time
///   numa versão anterior nunca subiu o próprio embrulho, então o servidor respondia 404 para o dono
///   e o segundo computador dele sorteava outra chave — cofre bifurcado, em silêncio.</item>
/// </list>
///
/// <para><b>Por que as duas nunca rodam juntas:</b> se a chave acabou de VIR do servidor, o servidor
/// já a tem — republicá-la seria um round-trip para reafirmar o que ele mesmo respondeu. E se não
/// veio, não há blob nenhum em disco para publicar.</para>
///
/// <para><b>Todo desfecho é ESCRITO no painel de Logs — sucesso e falha.</b> Um reparo silencioso
/// aqui seria a pior versão do defeito estrutural desta base: a chave chegaria (ou não) e o operador
/// continuaria olhando um aviso sem saber se o app tentou, se conseguiu, ou o que fazer a seguir. O
/// texto do aviso na barra (<c>VaultBadgeViewModel.TeamVaultNotActiveWarning</c>) aponta para cá.</para>
///
/// <para>Nada aqui é segredo (ADR-013): as linhas falam de desfecho, nunca de chave, blob ou token.</para>
/// </summary>
internal sealed class TeamKeyBootRepair
{
    private readonly IUiLogSink? _log;

    /// <param name="log">
    /// O painel de Logs. <c>null</c> é legítimo (o boot pode rodar antes de o container existir) e
    /// não é bandeira escondida — sem painel não há onde escrever, e o reparo em si continua valendo.
    /// </param>
    internal TeamKeyBootRepair(IUiLogSink? log) => _log = log;

    /// <summary>
    /// Roda o reparo e devolve se o cofre do time abre neste computador ao final. Quem chama usa a
    /// resposta para corrigir o indicador de cofre: a chave que aterrissa AGORA torna o aviso
    /// "a chave ainda não chegou" falso no mesmo instante, e um aviso falso é o que ensina o operador
    /// a ignorar os avisos verdadeiros.
    /// </summary>
    internal async Task<TeamVaultReadiness> RunAsync(TeamContext team, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(team);

        // Quem só tem cofre pessoal não paga NADA por este reparo: nem round-trip, nem uma linha de
        // log. É a maioria da frota, e ruído aqui apagaria os avisos que importam.
        if (!team.IsTeamSession)
        {
            return TeamVaultReadiness.NotTeamSession;
        }

        return team.SessionKind == SessionVaultKind.TeamWithoutKey
            ? await RestoreAsync(team, ct).ConfigureAwait(false)
            : await PublishAsync(team, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Busca na conta o embrulho da chave deste time. É a metade que faltava: o segundo computador do
    /// membro (e o do operador) só abre o cofre do time porque alguém vai buscar esse blob.
    /// </summary>
    private async Task<TeamVaultReadiness> RestoreAsync(TeamContext team, CancellationToken ct)
    {
        try
        {
            if (await team.Service.TryRestoreTeamKeyAsync(team.WorkspaceId, ct).ConfigureAwait(false))
            {
                _log?.Emit(
                    "[time] A chave deste time foi recuperada da sua conta: o cofre do time já abre "
                    + "neste computador.");
                return TeamVaultReadiness.KeyHere;
            }

            // O servidor respondeu, e a resposta foi "não tenho". Não é falha de rede — é a conta
            // realmente não guardar a chave deste time, o que tem causa e conserto próprios.
            _log?.Emit(
                "[time] A sua conta ainda não guarda a chave deste time, então nenhuma senha do time "
                + "abre ou é gravada neste computador. Se você foi convidado, aceite o convite "
                + "(identificador + código recebido por outro canal). Se você já é membro, abra o "
                + "RemoteOps no computador em que o cofre deste time funciona — é ele que registra a "
                + "chave na sua conta.");
            return TeamVaultReadiness.StillWithoutKey;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelamento DE VERDADE: o token DESTA chamada foi cancelado (o app fechando). Propaga
            // sem linha de log — a última tela não pode ganhar um aviso que não significa nada.
            throw;
        }
        catch (Exception ex)
        {
            // Falha vira TEXTO, nunca silêncio: sem esta linha o operador olharia o aviso da barra
            // sem saber se o app chegou a tentar. E ela distingue "o servidor não respondeu" de "a
            // resposta não serviu" — as duas pedem coisas diferentes dele.
            //
            // ⚠️ O timeout do HttpClient chega como TaskCanceledException (uma OCE) — e o boot chama
            // este reparo SEM token (ct = default), então ali TODO OCE é a rede pendurada, nunca o
            // chamador desistindo. Excluí-lo por tipo (`is not OperationCanceledException`) fazia a
            // forma MAIS comum de "servidor fora de alcance" numa rede de campo escapar para a Task
            // descartada do boot: nenhuma linha, com o aviso da barra prometendo o desfecho nos Logs.
            _log?.Emit(
                "[time] Não foi possível buscar a chave deste time na sua conta agora"
                + (ex is CloudSyncException or OperationCanceledException
                    ? " (servidor fora de alcance)."
                    : ".")
                + " Nenhuma senha do time abre neste computador enquanto isso; o RemoteOps tenta de "
                + "novo na próxima abertura.");
            return TeamVaultReadiness.StillWithoutKey;
        }
    }

    /// <summary>
    /// Publica o embrulho desta conta (1e′). Sem chave local sai antes de tocar a rede — e, num cofre
    /// de time COM chave, "sem chave local" não deveria acontecer: por isso o desfecho é KeyHere só
    /// quando o blob existe mesmo.
    /// </summary>
    private async Task<TeamVaultReadiness> PublishAsync(TeamContext team, CancellationToken ct)
    {
        try
        {
            TeamKeyUpload upload = await team.Service
                .PublishOwnWrappedKeyAsync(team.WorkspaceId, ct)
                .ConfigureAwait(false);

            if (upload == TeamKeyUpload.Published)
            {
                _log?.Emit(
                    "[time] A chave deste time foi registrada na sua conta. Agora ela pode ser "
                    + "recuperada nos seus outros computadores.");
            }

            return upload == TeamKeyUpload.NoLocalKey
                ? TeamVaultReadiness.StillWithoutKey
                : TeamVaultReadiness.KeyHere;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Mesma regra da outra metade: só o cancelamento do token DESTA chamada propaga calado.
            throw;
        }
        catch (Exception ex)
        {
            // O timeout do HttpClient é TaskCanceledException e cai AQUI de propósito (ver a outra
            // metade): no boot não há token, então OCE sem ct cancelado é a rede pendurada — e a
            // frase "(servidor fora de alcance)" abaixo é exatamente o recado certo para ele.
            // Sem detalhe técnico e sem blob (ADR-013): o texto é para o operador. Engolir calado
            // seria pior — este é o aviso de que o segundo computador dele pode não abrir o cofre do
            // time, ou de que a chave daqui não é a mesma da conta.
            _log?.Emit(
                "[time] Não foi possível registrar a chave deste time na sua conta agora"
                + (ex is TeamInviteException ? $": {ex.Message}" : " (servidor fora de alcance).")
                + " O RemoteOps tenta de novo na próxima abertura.");

            // A chave local CONTINUA aqui (a falha foi de publicação, não de cofre): o cofre do time
            // abre normalmente nesta sessão, e dizer o contrário acenderia um alarme falso.
            return TeamVaultReadiness.KeyHere;
        }
    }
}
