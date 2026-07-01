using System.Security.Cryptography;
using System.Text;

namespace RemoteOps.NDesk.Broker.Security;

/// <summary>
/// Gera e hasheia o link token do ticket NDesk. O valor cru só existe em memória, no retorno
/// da emissão e no redeem — nunca é persistido nem logado (CLAUDE.md princípio 1).
/// </summary>
internal static class NDeskTokenHasher
{
    private const int TokenByteLength = 32;

    public static string GenerateRawToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string Hash(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
