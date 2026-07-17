namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Normalização canônica de e-mail (trim + minúsculas, invariante).
///
/// Tem que ser a MESMA no registro, no login e no /auth/kdf: se o kdf resolvesse
/// o e-mail de forma diferente do registro, uma conta real cairia no salt decoy
/// de anti-enumeração e o login legítimo derivaria a MasterKey errada.
/// </summary>
public static class EmailNormalizer
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
