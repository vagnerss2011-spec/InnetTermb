namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Validação/decodificação do material E2EE que o cliente envia (salt, AuthHash, escrows e params do
/// Argon2id). Compartilhado por <see cref="AccountService"/> (register/change) e
/// <c>PasswordResetService</c> (reset por email): os dois têm que aplicar EXATAMENTE o mesmo piso de
/// custo e os mesmos tamanhos — senão o reset aceitaria material mais fraco que o registro e viraria
/// o elo fraco do escrow da conta.
/// </summary>
internal static class E2eeMaterialCodec
{
    public const int SaltLength = 16;
    public const int AuthHashLength = 32;

    // Piso de custo do Argon2id. Os params são escolhidos pelo device (perfil da máquina), mas sem
    // um piso um cliente adulterado registraria/resetaria com custo ~0 e enfraqueceria o escrow da
    // própria conta. 19 MiB = mínimo OWASP p/ Argon2id.
    public const int MinMemoryKib = 19456;
    public const int MinIterations = 2;

    public static void ValidateParams(Argon2Params p)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (p.MemoryKib < MinMemoryKib)
            throw new ArgumentException($"argon2Params.memoryKib abaixo do mínimo ({MinMemoryKib}).");
        if (p.Iterations < MinIterations)
            throw new ArgumentException($"argon2Params.iterations abaixo do mínimo ({MinIterations}).");
        if (p.Parallelism < 1)
            throw new ArgumentException("argon2Params.parallelism deve ser >= 1.");
        if (p.OutputBytes != 32)
            throw new ArgumentException("argon2Params.outputBytes deve ser 32 (a MasterKey é de 32B).");
    }

    public static byte[] DecodeExact(string b64, int expectedLength, string field)
    {
        var bytes = DecodeNonEmpty(b64, field);
        if (bytes.Length != expectedLength)
            throw new ArgumentException($"{field} deve ter {expectedLength} bytes.");
        return bytes;
    }

    public static byte[] DecodeNonEmpty(string b64, string field)
    {
        if (string.IsNullOrWhiteSpace(b64))
            throw new ArgumentException($"{field} é obrigatório.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ArgumentException($"{field} não é base64 válido."); }
        if (bytes.Length == 0)
            throw new ArgumentException($"{field} não pode ser vazio.");
        return bytes;
    }
}
