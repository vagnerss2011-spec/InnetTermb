namespace RemoteOps.Sync.Remote;

/// <summary>
/// Transporte dos <c>SecretEnvelope</c> (spec §5) — o canal PRÓPRIO do segredo, fora do changelog de
/// metadados (que continua recusando <c>SecretEnvelope</c> de propósito: ver
/// <see cref="LocalEntitiesChangeApplier"/> e ADR-003).
///
/// <para>Tudo que passa por aqui é base64 OPACO. Nenhuma implementação desta interface decifra,
/// valida conteúdo ou inspeciona segredo — ela move bytes que só a WDK (derivada da AMK, no device)
/// consegue abrir.</para>
/// </summary>
public interface ISecretsApi
{
    /// <summary>
    /// Sobe envelopes (<c>POST /secrets</c>).
    ///
    /// <para>O backend NÃO tem endpoint de lote — o corpo real é <c>{workspaceId, envelope}</c>, um
    /// envelope por request. A lista aqui é conveniência do chamador: a implementação faz o fan-out
    /// e devolve UM resultado por envelope, na mesma ordem.</para>
    /// </summary>
    Task<IReadOnlyList<SecretUpsertResult>> PushAsync(
        string workspaceId, IReadOnlyList<SecretEnvelopeDto> envelopes, CancellationToken ct = default);

    /// <summary>Puxa uma página de envelopes com cursor &gt; <paramref name="since"/> (<c>GET /secrets</c>).</summary>
    Task<SecretsPullResponse> PullAsync(
        string workspaceId, long since, int pageSize, CancellationToken ct = default);
}
