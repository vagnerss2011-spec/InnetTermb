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
}
