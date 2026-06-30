namespace RemoteOps.Security.Vault;

/// <summary>
/// Registro persistido de um segredo cifrado via envelope encryption.
/// NUNCA contém plaintext nem a chave de workspace — apenas ciphertext,
/// a CEK embrulhada (wrapped) pela Workspace Data Key e metadados.
/// </summary>
public sealed record SecretEnvelope
{
    public required string EnvelopeId { get; init; }

    public required string WorkspaceId { get; init; }

    public required string CredentialId { get; init; }

    /// <summary>password | privateKey | secret | ...</summary>
    public required string Type { get; init; }

    public required int Version { get; init; }

    /// <summary>Identificador do esquema de criptografia usado.</summary>
    public required string Algorithm { get; init; }

    /// <summary>CEK (Content Encryption Key) cifrada pela Workspace Data Key.</summary>
    public required byte[] WrappedCek { get; init; }

    public required byte[] CekNonce { get; init; }

    public required byte[] CekTag { get; init; }

    /// <summary>Segredo cifrado pela CEK. Inútil sem a WDK protegida por DPAPI.</summary>
    public required byte[] Ciphertext { get; init; }

    public required byte[] Nonce { get; init; }

    public required byte[] Tag { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Quando preenchido, o envelope está revogado e não pode ser aberto.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>ToString redigido: nunca expõe material criptográfico.</summary>
    public override string ToString() =>
        $"SecretEnvelope(Id={EnvelopeId}, Workspace={WorkspaceId}, Credential={CredentialId}, v{Version}, Revoked={RevokedAt is not null})";
}
