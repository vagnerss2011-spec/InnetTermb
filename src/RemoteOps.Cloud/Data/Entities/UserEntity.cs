namespace RemoteOps.Cloud.Data.Entities;

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public required string Status { get; set; }
    public bool MfaRequired { get; set; }

    /// <summary>
    /// Hash da senha do fluxo LEGADO (pré-E2EE). Nulo em contas E2EE, que autenticam
    /// por AuthHash. Mantido para não quebrar contas já criadas.
    /// </summary>
    public string? PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // ── E2EE (ADR-003 / spec cloud-sync-e2ee-phase1 §4.2) ────────────────────
    // Tudo aqui é público ou opaco. O servidor NUNCA vê senha, MasterKey, KEK,
    // AMK, WDK, CEK nem plaintext.

    /// <summary>Salt do Argon2id, 16B CSPRNG por conta. Público — o device precisa dele para derivar a MasterKey.</summary>
    public byte[]? Argon2Salt { get; set; }

    /// <summary>Memória do Argon2id em KiB (v1 = 65536 = 64 MiB). Público.</summary>
    public int Argon2MemoryKib { get; set; }

    /// <summary>Iterações do Argon2id (v1 = 3). Público.</summary>
    public int Argon2Iterations { get; set; }

    /// <summary>Paralelismo do Argon2id (v1 = 1). Público.</summary>
    public int Argon2Parallelism { get; set; }

    /// <summary>Bytes de saída do Argon2id (v1 = 32). Público.</summary>
    public int Argon2OutputBytes { get; set; }

    /// <summary>
    /// PBKDF2-SHA256 do AuthHash que o cliente envia. O AuthHash cru NUNCA é
    /// persistido: um dump do banco não pode ser replayado como prova de senha.
    /// </summary>
    public string? AuthHashHash { get; set; }

    /// <summary>Escrow da AMK embrulhada pela KEK (derivada da senha). Opaco: AES-256-GCM feito no device.</summary>
    public byte[]? WrappedAmkPwd { get; set; }

    /// <summary>Escrow da AMK embrulhada pela chave de recuperação. Opaco.</summary>
    public byte[]? WrappedAmkRec { get; set; }

    /// <summary>Versão da AMK. Não muda em troca de senha — só re-embrulha.</summary>
    public int AmkKeyVersion { get; set; }

    public ICollection<MembershipEntity> Memberships { get; set; } = [];
    public ICollection<DeviceEntity> Devices { get; set; } = [];
    public ICollection<RefreshTokenEntity> RefreshTokens { get; set; } = [];
}
