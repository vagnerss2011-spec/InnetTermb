namespace RemoteOps.Sync.Remote;

/// <summary>
/// Conjunto de tokens emitidos pelo Cloud para um device. Nunca é logado nem persistido
/// em texto puro fora do vault (ADR-003/ADR-013).
/// </summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    // ToString redatado: o record geraria automaticamente os tokens em claro, o que vazaria num
    // eventual `logger.Log("{t}", tokenSet)`. Só a expiração é exposta (ADR-013, no-secret-in-log).
    public override string ToString() => $"TokenSet(ExpiresAt={ExpiresAt:O})";
}
