namespace RemoteOps.Sync.Remote;

/// <summary>
/// <c>SecretEnvelope</c> no fio (spec §5). Espelha o DTO REAL do backend
/// (<c>remoteops-cloud@a94fb1e</c>, <c>Secrets/SecretsModels.cs</c>) — pinado pelo
/// <c>SecretsContractsWireTests</c>.
///
/// <para>TODO campo binário é base64 de um blob OPACO: o servidor não tem a WDK nem a CEK, então não
/// interpreta — só guarda e devolve igual. O transporte do cliente também nunca decifra: ele move
/// bytes.</para>
///
/// <para><b>Divergências conhecidas em relação ao <c>SecretEnvelope</c> do cofre</b> (ver
/// <see cref="SecretEnvelopeWireCodec"/> para o tratamento de cada uma): o DTO não tem
/// <c>CredentialId</c>, <c>Type</c>, <c>CreatedAt</c> nem <c>RevokedAt</c>, e tem um
/// <c>KeyVersion</c> que o cofre não tem.</para>
/// </summary>
public sealed record SecretEnvelopeDto(
    string Id,
    string WorkspaceId,
    string Ciphertext,
    string Nonce,
    string Tag,
    string WrappedCek,
    string CekNonce,
    string CekTag,
    string KeyVersion,
    int Version)
{
    /// <summary>Algoritmo declarado pelo cliente. O servidor assume AES-256-GCM se vier nulo.</summary>
    public string? Algorithm { get; init; }

    /// <summary>
    /// Quando preenchido, este envelope é um TOMBSTONE: foi revogado e o material veio zerado de
    /// propósito. É o único caso em que o servidor aceita base64 vazio.
    ///
    /// <para><b>Opcional de propósito</b> (regra de ouro: o formato novo ADICIONA, nunca troca). Um
    /// servidor antigo ignora o campo; um cliente antigo que baixe um tombstone não vê a marca, mas
    /// grava o material ZERADO que veio junto — ou seja, mesmo sem entender a revogação ele deixa de
    /// conseguir abrir o segredo. Ninguém quebra, e o desfecho de segurança é o mesmo.</para>
    /// </summary>
    public DateTimeOffset? RevokedAt { get; init; }
}

/// <summary>Página de <c>GET /secrets</c>.</summary>
public sealed record SecretsPullResponse(
    IReadOnlyList<SecretEnvelopeDto> Envelopes,
    long NextCursor,
    bool HasMore);

/// <summary>Resultado do upsert. <c>Status</c>: "ok" | "conflict".</summary>
public sealed record SecretUpsertResult(
    string Status,
    long Cursor,
    int? CurrentVersion = null,
    string? Reason = null);

/// <summary>
/// Corpo de <c>POST /secrets</c>. Note o singular: o backend aceita UM envelope por request — não
/// existe endpoint de lote (ver <see cref="ISecretsApi.PushAsync"/>).
/// </summary>
public sealed record SecretsUpsertRequest(string WorkspaceId, SecretEnvelopeDto Envelope);
