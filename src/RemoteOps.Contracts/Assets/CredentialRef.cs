namespace RemoteOps.Contracts.Assets;

public sealed class CredentialRef
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>password | privateKey | ...</summary>
    public required string Type { get; init; }

    public string? Scope { get; init; }

    public CredentialMetadata? Metadata { get; init; }

    /// <summary>Referência ao envelope criptografado. Nunca expõe o segredo real.</summary>
    public string? SecretEnvelopeId { get; init; }

    public int Version { get; init; }
}

public sealed class CredentialMetadata
{
    public string? Username { get; init; }

    public bool HasPrivateKey { get; init; }

    /// <summary>Envelope da passphrase da chave privada (quando houver); null = chave sem passphrase.</summary>
    public string? PassphraseEnvelopeId { get; init; }

    public DateTimeOffset? LastRotatedAt { get; init; }
}
