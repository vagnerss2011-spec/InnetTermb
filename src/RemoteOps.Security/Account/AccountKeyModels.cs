namespace RemoteOps.Security.Account;

/// <summary>Parâmetros públicos do Argon2id (guardados por conta no servidor; necessários pra re-derivar a chave num device novo).</summary>
public sealed record Argon2Params(int MemoryKib, int Iterations, int Parallelism, int OutputBytes)
{
    /// <summary>Perfil v1: 64 MiB, 3 iterações, 1 lane, saída 32B.</summary>
    public static Argon2Params Default { get; } = new(65536, 3, 1, 32);
}

/// <summary>Derivados da senha (SÓ no device). AuthHash vai pro servidor; Kek nunca sai.</summary>
public sealed record AccountKeyMaterial(byte[] AuthHash, byte[] Kek);

/// <summary>
/// Resultado de <see cref="AccountKeyService.Enroll"/>. <see cref="Amk"/> é a raiz portável do cofre
/// (o chamador usa pra rootar a WDK e zera depois). Os <c>Wrapped*</c> + <see cref="AuthHash"/> +
/// <see cref="Argon2Salt"/>/<see cref="Params"/> são o que sobe pro servidor (opaco). A
/// <see cref="RecoveryKey"/> é exibida UMA vez ao operador.
/// </summary>
public sealed record AccountEnrollment(
    byte[] Amk,
    byte[] Argon2Salt,
    Argon2Params Params,
    byte[] AuthHash,
    byte[] WrappedAmkPwd,
    byte[] WrappedAmkRec,
    string RecoveryKey);
