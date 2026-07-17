using System.Security.Cryptography;
using System.Text;

namespace RemoteOps.Cloud.Auth;

/// <summary>
/// PBKDF2-SHA256 (OWASP 2023: 310 000 iterações). Formato: "v1:salt_b64:hash_b64".
///
/// Usado em dois lugares com o MESMO formato:
///  - senha legada (fluxo pré-E2EE);
///  - AuthHash das contas E2EE — o cliente já gastou Argon2id na senha, mas o
///    servidor ainda assim não guarda o AuthHash cru, senão um dump do banco
///    viraria prova de senha replayável contra /auth/login.
///
/// Extraído do TokenService (era `file class BCryptNet`) para ser compartilhado
/// com o AccountService. O nome BCrypt era enganoso: sempre foi PBKDF2.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 310_000;
    private const int HashLength = 32;
    private const int SaltLength = 16;

    public static string Hash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(value), salt,
            Iterations, HashAlgorithmName.SHA256, HashLength);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string value, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        var parts = hash.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(value), salt,
            Iterations, HashAlgorithmName.SHA256, HashLength);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
