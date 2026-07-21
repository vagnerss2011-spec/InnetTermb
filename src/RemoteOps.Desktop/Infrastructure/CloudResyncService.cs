using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>Progresso do reenvio: <paramref name="Completed"/> de <paramref name="Total"/> itens.</summary>
public readonly record struct ResyncProgress(int Completed, int Total);

/// <summary>
/// Resultado do reenvio. <paramref name="Ran"/> é <c>false</c> quando não havia nuvem — o operador
/// precisa saber a diferença entre "reenviei zero itens" e "nem tentei".
/// </summary>
public sealed record ResyncResult(bool Ran, int ReEmitted, int Failed);

/// <summary>
/// "Reenviar tudo para a nuvem": re-emite TODO o acervo local pelo caminho de update normal, para
/// reparar os patches incompletos que ficaram congelados na fila por versões antigas do app.
///
/// <para><b>Por que é preciso reenviar:</b> o outbox congela o patch no MOMENTO da edição (o
/// <c>LocalSyncClient</c> serializa o patch em <c>patch_json</c> e o envio relê esse blob, sem
/// reconstruir do registro atual). Os hosts cadastrados numa versão com o bug "endpoint sobe sem
/// <c>credential_ref_id</c>" gravaram patches incompletos que ficaram incompletos PARA SEMPRE — e
/// foram drenados assim ao ligar a nuvem. Corrigir o código que monta o patch não desfaz o que já
/// está gravado no servidor; só um reenvio desfaz.</para>
///
/// <para><b>Por que pelo <c>Update*Async</c> e não por um push cru:</b> o caminho de update já lê
/// <c>baseVersion</c> = versão local atual e monta o patch completo com o código de HOJE. Nada de
/// mexer em <c>base_version</c> na mão.</para>
///
/// <para><b>Por que o pull vem primeiro:</b> o re-emit sobe com a versão LOCAL como base, e o servidor
/// rejeita <c>base_version &lt; versão dele</c> como conflito. Sem alinhar antes, um acervo de
/// centenas de devices viraria centenas de conflitos em vez de reparo. Depois do pull a versão local
/// é a do servidor, o push sobe com <c>base_version == servidor</c> (não é <c>&lt;</c>) → aceito →
/// versão+1 → propaga o dado completo ao outro device.</para>
///
/// <para><b>Não toca em envelopes</b> (ADR-003): o canal de segredos já enumera o cofre inteiro a cada
/// ciclo, então as senhas sobem sozinhas. Aqui só andam METADADOS — e nenhum ponto novo de
/// decifração. O <c>secret_envelope_id</c> que viaja nos patches é REFERÊNCIA, não segredo.</para>
/// </summary>
public sealed class CloudResyncService
{
    private readonly ILocalStore _store;
    private readonly string _workspaceId;
    private readonly ISyncController? _sync;

    /// <param name="sync">
    /// Controlador do ciclo de sync, ou <c>null</c> em modo local (sem nuvem) — nesse caso o reenvio
    /// não roda: não há para onde reenviar (offline-first, ADR-002).
    /// </param>
    public CloudResyncService(ILocalStore store, string workspaceId, ISyncController? sync)
    {
        _store = store;
        _workspaceId = workspaceId;
        _sync = sync;
    }

    /// <summary>Há nuvem para onde reenviar? Falso em modo local — a UI desabilita o botão.</summary>
    public bool CanResync => _sync is not null;

    /// <summary>
    /// Re-emite o acervo inteiro. Ordem: pull → grupos → ativos → endpoints → credenciais → drenagem.
    ///
    /// <para><b>Isolamento por item:</b> uma linha que falhe é PULADA e contada, nunca aborta o
    /// reparo das outras — é a mesma lição do canal de segredos: um item problemático não pode levar
    /// junto o acervo inteiro. O que falhou aparece no resultado (e na tela): reparo parcial
    /// silencioso seria trocar um problema por outro.</para>
    ///
    /// <para>Nada é logado por item (ADR-013). O que a UI mostra é contagem — nem id de entidade, nem
    /// mensagem de exceção, que poderiam carregar dado do operador.</para>
    /// </summary>
    public async Task<ResyncResult> ResyncAllAsync(
        IProgress<ResyncProgress>? progress = null, CancellationToken ct = default)
    {
        if (_sync is not { } sync)
        {
            return new ResyncResult(Ran: false, ReEmitted: 0, Failed: 0);
        }

        // 1) PULL primeiro — o passo que transforma "700 conflitos" em "700 reparos". Ver o resumo
        // da classe: o servidor recusa base_version menor que a dele.
        //
        // E se o pull FALHOU, aborta ANTES de re-emitir qualquer coisa: o orquestrador engole a
        // falha do ciclo (offline-first) e só a devolve neste bool. Prosseguir seria encher o outbox
        // de patches com base_version possivelmente atrasada — exatamente a enxurrada de conflitos
        // que o pull inicial existe para impedir — e ainda dizer "concluído" na tela com a nuvem
        // fora do ar. Relançar aqui é seguro: nada saiu do lugar, e a VM traduz em "não foi possível".
        if (!await sync.SyncNowAsync(ct))
        {
            throw new InvalidOperationException(
                "O sync inicial do reenvio falhou; nada foi re-emitido.");
        }

        // Lido DEPOIS do pull, de propósito: é o acervo já reconciliado com o servidor que se re-emite.
        IReadOnlyList<AssetGroup> groups = await _store.GetGroupsAsync(_workspaceId, ct);
        IReadOnlyList<Asset> assets = await _store.GetAssetsAsync(_workspaceId, ct: ct);
        IReadOnlyList<CredentialRef> credentials = await _store.GetCredentialRefsAsync(_workspaceId, ct);
        List<Endpoint> endpoints = assets.SelectMany(a => a.Endpoints).ToList();

        int total = groups.Count + assets.Count + endpoints.Count + credentials.Count;
        int completed = 0;
        int reEmitted = 0;
        int failed = 0;

        // Primeiro relatório antes de qualquer update: a tela já mostra "0 de N" e o operador vê o
        // tamanho da tarefa em vez de uma barra parada sem número.
        progress?.Report(new ResyncProgress(0, total));

        async Task ReEmitAsync(Func<Task> update)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await update();
                reEmitted++;
            }
            catch (OperationCanceledException)
            {
                throw; // cancelamento é do operador, não é item venenoso.
            }
            catch (Exception)
            {
                failed++;
            }

            completed++;
            progress?.Report(new ResyncProgress(completed, total));
        }

        // A ordem (grupos → ativos → endpoints → credenciais) segue a hierarquia do acervo. Não há
        // chave estrangeira no banco local nem no changelog, então ela é só higiene: o applier do
        // outro device faz upsert item a item, em qualquer ordem.
        foreach (AssetGroup group in groups)
        {
            await ReEmitAsync(() => _store.UpdateGroupAsync(group, ct));
        }

        foreach (Asset asset in assets)
        {
            await ReEmitAsync(() => _store.UpdateAssetAsync(asset, ct));
        }

        foreach (Endpoint endpoint in endpoints)
        {
            // O endpoint é o item que motivou tudo isto: é ele que carrega o credential_ref_id que
            // chegou nulo no outro device ("o endpoint não tem credencial").
            await ReEmitAsync(() => _store.UpdateEndpointAsync(endpoint, ct));
        }

        foreach (CredentialRef credential in credentials)
        {
            await ReEmitAsync(() => _store.UpdateCredentialRefAsync(credential, ct));
        }

        // 3) Drena o outbox agora, em vez de esperar o laço de fundo: o operador clicou no botão para
        // ver o problema resolvido, não para ficar sem saber se subiu.
        //
        // Drenagem falhou = o reenvio NÃO chegou à nuvem, e dizer "concluído" seria a mentira mais
        // cara da tela. Relançar é seguro: os patches ficam no outbox com a versão JÁ alinhada pelo
        // pull inicial (o laço de fundo os entrega quando a rede voltar), e clicar de novo é
        // idempotente — o novo pull realinha e o re-emit só soma versão.
        if (!await sync.SyncNowAsync(ct))
        {
            throw new InvalidOperationException(
                "A drenagem final do reenvio falhou; o envio será retomado pelo sync de fundo.");
        }

        return new ResyncResult(Ran: true, reEmitted, failed);
    }
}
