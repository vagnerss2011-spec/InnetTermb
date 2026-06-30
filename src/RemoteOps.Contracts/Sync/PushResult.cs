namespace RemoteOps.Contracts.Sync;

/// <summary>
/// Resultado do <c>POST /sync/push</c>.
/// <para><see cref="Status"/> é <c>"ok"</c> quando todas as mudanças foram aplicadas, ou
/// <c>"conflict"</c> quando ao menos uma foi rejeitada — ver <see cref="Conflicts"/>.</para>
/// </summary>
/// <param name="Status"><c>"ok"</c> | <c>"conflict"</c>.</param>
/// <param name="NewCursor">Maior id de changelog gerado por este push (ou <c>null</c> se nada foi aplicado).</param>
/// <param name="Conflicts">Conflitos detectados pelo servidor; <c>null</c>/vazio quando <see cref="Status"/> é <c>"ok"</c>.</param>
public sealed record PushResult(
    string Status,
    long? NewCursor,
    IReadOnlyList<ConflictDetail>? Conflicts = null);
