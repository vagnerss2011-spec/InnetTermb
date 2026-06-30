namespace RemoteOps.Cloud.Data.Entities;

/// <summary>
/// Referência opaca de envelope no servidor. Armazena apenas ciphertext e metadados.
/// O servidor NUNCA possui a Workspace Data Key (WDK) nem a Content Encryption Key (CEK).
/// Conforme ADR-003 e ADR-010.
/// </summary>
public sealed class SecretEnvelopeEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>Blob criptografado enviado pelo cliente. Opaco para o servidor.</summary>
    public required byte[] Ciphertext { get; set; }

    public required byte[] Nonce { get; set; }

    /// <summary>Tag de autenticação AES-GCM.</summary>
    public required byte[] Tag { get; set; }

    public required string Algorithm { get; set; }

    /// <summary>Versão da chave de workspace usada pelo cliente para embrulhar a CEK.</summary>
    public required string KeyVersion { get; set; }

    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
}
