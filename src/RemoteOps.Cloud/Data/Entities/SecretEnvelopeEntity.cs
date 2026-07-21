namespace RemoteOps.Cloud.Data.Entities;

/// <summary>
/// Referência opaca de envelope no servidor. Armazena apenas ciphertext e metadados.
/// O servidor NUNCA possui a Workspace Data Key (WDK) nem a Content Encryption Key (CEK).
/// Conforme ADR-003 e ADR-008.
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

    /// <summary>CEK embrulhada pela WDK. Opaca: o servidor não tem a WDK para abrir.</summary>
    public byte[]? WrappedCek { get; set; }

    /// <summary>Nonce do AES-GCM usado para embrulhar a CEK.</summary>
    public byte[]? CekNonce { get; set; }

    /// <summary>Tag do AES-GCM usado para embrulhar a CEK.</summary>
    public byte[]? CekTag { get; set; }

    public required string Algorithm { get; set; }

    /// <summary>Versão da chave de workspace usada pelo cliente para embrulhar a CEK.</summary>
    public required string KeyVersion { get; set; }

    /// <summary>
    /// Versão do envelope, monotônica por id. Um upsert com versão &lt;= à atual é
    /// recusado como conflito — evita que um device atrasado sobrescreva uma rotação.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Cursor monotônico por workspace, atribuído a CADA upsert (inclusive updates).
    /// É o que faz `GET /secrets?since=` devolver envelopes rotacionados: o Id não
    /// muda no update, então ele não serviria de cursor.
    /// </summary>
    public long Cursor { get; set; }

    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>
    /// Quando preenchido, o envelope está REVOGADO: o cliente trocou/apagou a senha e subiu a
    /// lápide com o material zerado. O servidor não interpreta o motivo — só propaga a marca, que é
    /// o que faz o outro device apagar a cópia dele.
    ///
    /// <para>É um caminho SÓ DE IDA: um upsert vivo por cima de um envelope revogado é recusado
    /// (<c>envelope.revoked</c>), senão a senha velha voltaria a existir em todos os devices.</para>
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
