namespace RemoteOps.Contracts.Sync;

/// <summary>
/// Resposta do <c>GET /sync/pull</c>: página do changelog do servidor a partir de um cursor.
/// </summary>
/// <param name="Changes">Mudanças aplicáveis localmente, em ordem crescente de cursor.</param>
/// <param name="NextCursor">Cursor a usar na próxima chamada (id do último item desta página).</param>
/// <param name="HasMore">Indica se há mais páginas além desta.</param>
public sealed record PullResponse(
    IReadOnlyList<SyncChange> Changes,
    long NextCursor,
    bool HasMore);
