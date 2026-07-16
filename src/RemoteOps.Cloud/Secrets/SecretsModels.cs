namespace RemoteOps.Cloud.Secrets;

/// <summary>
/// SecretEnvelope no fio. TODO campo binário é base64 de um blob OPACO: o servidor
/// não tem a WDK nem a CEK, então não interpreta — só guarda e devolve igual.
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
    /// <summary>Algoritmo declarado pelo cliente. Default: AES-256-GCM (ADR-003).</summary>
    public string? Algorithm { get; init; }
}

public sealed record SecretsPullResponse(
    IReadOnlyList<SecretEnvelopeDto> Envelopes,
    long NextCursor,
    bool HasMore);

/// <summary>Resultado do upsert. Status: "ok" | "conflict".</summary>
public sealed record SecretUpsertResult(
    string Status,
    long Cursor,
    int? CurrentVersion = null,
    string? Reason = null);

public sealed record SecretsUpsertRequest(string WorkspaceId, SecretEnvelopeDto Envelope);
