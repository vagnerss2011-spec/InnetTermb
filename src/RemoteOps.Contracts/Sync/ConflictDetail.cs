namespace RemoteOps.Contracts.Sync;

/// <summary>
/// Detalhe de um conflito devolvido pelo servidor durante o push. Nunca contém segredo nem patch:
/// para <c>SecretEnvelope</c> o servidor sempre devolve <see cref="Reason"/>
/// <c>"secret-envelope.no-auto-merge"</c> (sem auto-merge), e para versão obsoleta
/// <c>"version.conflict"</c>. O cliente registra o conflito e nunca resolve segredos automaticamente.
/// </summary>
/// <param name="ClientChangeId">Correlaciona o conflito à mudança local que o originou (se houver).</param>
/// <param name="EntityType">Tipo da entidade em conflito.</param>
/// <param name="EntityId">Id da entidade em conflito.</param>
/// <param name="BaseVersion">Versão base que o cliente enviou.</param>
/// <param name="CurrentVersion">Versão atual no servidor (<c>-1</c> quando não aplicável, ex.: SecretEnvelope).</param>
/// <param name="Reason">Motivo estável do conflito (ex.: <c>"version.conflict"</c>, <c>"secret-envelope.no-auto-merge"</c>).</param>
public sealed record ConflictDetail(
    string? ClientChangeId,
    string EntityType,
    string EntityId,
    int BaseVersion,
    int CurrentVersion,
    string Reason);
