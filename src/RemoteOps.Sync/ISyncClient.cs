using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync;

// TODO: Implementar na frente feature/sync-local.
// Outbox local em SQLite + push/pull com cursor para o Cloud API.
public interface ISyncClient
{
    long CurrentCursor { get; }

    /// <summary>
    /// Disparado quando um <see cref="PushAsync"/> GRAVOU ao menos uma mudança local no outbox (Fase 2,
    /// item A — "push-ao-mudar"). É o gatilho pra um sync incremental logo após uma edição, em vez de
    /// esperar o próximo tick de ~2 min. Só o <c>SqlCipherLocalStore</c> escreve no outbox (o applier do
    /// pull NÃO usa PushAsync, pra não ecoar), então este evento marca exatamente uma edição do operador.
    ///
    /// <para><b>Levantado na thread que chamou <see cref="PushAsync"/></b> (a de UI, quando o store
    /// grava uma edição). Quem consome DEVE agrupar (debounce) e sincronizar fora dela — nunca bloquear
    /// a edição esperando a rede.</para>
    /// </summary>
    event Action? LocalChangePushed;

    Task PushAsync(IEnumerable<SyncChange> changes, CancellationToken ct = default);

    Task<IReadOnlyList<SyncChange>> PullAsync(long fromCursor, int limit = 500, CancellationToken ct = default);
}
