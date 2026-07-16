using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>Cursores de sync persistidos: do servidor (changelog) e do outbox enviado.</summary>
public readonly record struct SyncCursors(long ServerCursor, long OutboxCursor);

/// <summary>
/// Persiste os cursores de sync e os conflitos (<see cref="ConflictDetail"/>) — na prática, no
/// mesmo banco SQLCipher do workspace (ADR-013). O conflito nunca contém segredo nem patch sensível.
/// </summary>
public interface ISyncMetadataStore
{
    Task<SyncCursors> GetCursorsAsync(string workspaceId, CancellationToken ct = default);

    Task SaveServerCursorAsync(string workspaceId, long cursor, CancellationToken ct = default);

    Task SaveOutboxCursorAsync(string workspaceId, long cursor, CancellationToken ct = default);

    Task RecordConflictsAsync(IReadOnlyList<ConflictDetail> conflicts, CancellationToken ct = default);

    Task<int> GetConflictCountAsync(CancellationToken ct = default);

    // ── Canal de segredos (spec §5) ──────────────────────────────────────────────────────
    // Cursores e ledger próprios: o SecretEnvelope viaja FORA do changelog, então tem estado de
    // sync próprio. Nada aqui guarda material de envelope — só id, versão e posição.

    /// <summary>Cursor do servidor no canal de segredos (<c>GET /secrets?since=</c>).</summary>
    Task<long> GetSecretsCursorAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>Avança o cursor de segredos. Monotônico: um save tardio nunca regride.</summary>
    Task SaveSecretsCursorAsync(string workspaceId, long cursor, CancellationToken ct = default);

    /// <summary>
    /// Ledger do que já está no servidor: <c>envelopeId → maior versão confirmada</c>. É o que evita
    /// re-subir o cofre inteiro a cada ciclo e devolver como eco o que acabou de ser baixado.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetPushedSecretsAsync(
        string workspaceId, CancellationToken ct = default);

    /// <summary>Marca um envelope como presente no servidor na versão informada. Monotônico.</summary>
    Task MarkSecretPushedAsync(
        string workspaceId, string envelopeId, int version, CancellationToken ct = default);
}
