using System.Security.Cryptography;
using System.Text;

namespace RemoteOps.Security.Account;

/// <summary>
/// Deriva a WDK (Workspace Data Key) da AMK portável, de forma DETERMINÍSTICA por workspace
/// (HKDF-SHA256). Assim, qualquer device que tenha a AMK deriva a MESMA WDK — a raiz deixa de ser
/// aleatória-por-máquina (DPAPI) e passa a viajar junto com a AMK, sem precisar sincronizar um blob
/// de WDK. A camada WDK→CEK→segredo (EnvelopeCipher) continua igual. Ver spec §4.1.
/// </summary>
public static class AmkKeyDerivation
{
    private const int KeySize = 32;

    public static byte[] DeriveWorkspaceKey(byte[] amk, string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(amk);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        byte[] info = Encoding.UTF8.GetBytes("remoteops:wdk:v1:" + workspaceId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, amk, KeySize, salt: null, info: info);
    }
}
