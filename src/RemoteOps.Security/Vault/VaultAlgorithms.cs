namespace RemoteOps.Security.Vault;

/// <summary>
/// Vocabulário do campo <see cref="SecretEnvelope.Algorithm"/>: identifica o esquema de cripto e,
/// principalmente, a RAIZ de chave sob a qual o envelope está selado. Público porque
/// <c>Algorithm</c> é um campo público do envelope — o <c>EnvelopeCipher</c> continua internal.
/// </summary>
public static class VaultAlgorithms
{
    /// <summary>
    /// Legado (pré-E2EE): WDK aleatória por máquina, protegida por DPAPI CurrentUser.
    /// Valor imutável — envelopes já gravados em disco carregam exatamente esta string.
    /// </summary>
    public const string DpapiRootedV1 = "AES-256-GCM;CEK-wrap;DPAPI-CurrentUser";

    /// <summary>E2EE Fase 1: WDK = HKDF-SHA256(AMK, workspaceId). Portável entre devices.</summary>
    public const string AmkRootedV1 = "AES-256-GCM;CEK-wrap;AMK-HKDF-v1";

    /// <summary>
    /// Time (Fatia 1): a chave do workspace é ALEATÓRIA e compartilhada — não deriva de ninguém.
    /// Precisa ser assim: derivar da AMK (que é por CONTA) faria cada membro do mesmo workspace
    /// chegar a uma chave diferente, e o colega não decifraria nada. Sendo sorteada, ela pode ser
    /// entregue cifrada a cada membro; em disco fica embrulhada sob a AMK de quem a guarda.
    ///
    /// <para><b>O AAD deste esquema é MAIOR</b> — prende também o <c>CredentialId</c> e o próprio
    /// <c>Algorithm</c> (ver <c>EnvelopeCipher.BuildAad</c>). Só vale para envelopes NOVOS: mexer no
    /// AAD do <see cref="AmkRootedV1"/> tornaria ilegível tudo o que já está selado em produção.</para>
    /// </summary>
    public const string WkRootedV1 = "AES-256-GCM;CEK-wrap;WK-random-v1";
}

/// <summary>
/// Raiz de chave do cofre local de um workspace — a "versão de key-rooting" que a migração
/// registra para ser idempotente.
/// </summary>
public enum VaultKeyRooting
{
    /// <summary>WDK aleatória protegida por DPAPI (pré-E2EE).</summary>
    DpapiRandom = 0,

    /// <summary>WDK derivada da AMK portável (E2EE Fase 1).</summary>
    AmkDerived = 1,

    /// <summary>
    /// WK aleatória do workspace, compartilhada pelo time (Fatia 1). Valor NOVO no fim do enum: o
    /// <c>FileVaultStore</c> persiste a raiz como int, então renumerar os existentes reetiquetaria
    /// cofres já gravados.
    /// </summary>
    WkRandom = 2,
}
