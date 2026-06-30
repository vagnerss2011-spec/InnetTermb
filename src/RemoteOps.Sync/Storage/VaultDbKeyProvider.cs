using System.Security.Cryptography;

using RemoteOps.Security;

namespace RemoteOps.Sync.Storage;

/// <summary>
/// Obtém ou cria a chave AES-256 do banco local via <see cref="ICredentialVault"/>
/// (envelope encryption + DPAPI, ADR-003).
///
/// Fluxo:
///  1ª abertura → gera 32 bytes CSPRNG, converte para hex, armazena no vault,
///                salva o envelopeId (não o segredo) em <see cref="_keyRefPath"/>.
///  Reabertura  → lê envelopeId do arquivo .keyref, recupera hex do vault.
///
/// O hexKey resultante (string) tem a limitação conhecida de não poder ser zerado
/// (contrato thin string cross-module); aceita-se conforme ADR-003 § Limitações.
/// </summary>
internal sealed class VaultDbKeyProvider : IDbKeyProvider
{
    private readonly ICredentialVault _vault;
    private readonly string _keyRefPath;

    internal VaultDbKeyProvider(ICredentialVault vault, string keyRefPath)
    {
        _vault = vault;
        _keyRefPath = keyRefPath;
    }

    public async Task<string> GetOrCreateKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        if (File.Exists(_keyRefPath))
        {
            string envelopeId = (await File.ReadAllTextAsync(_keyRefPath, ct)).Trim();
            string? hexKey = await _vault.RetrieveSecretAsync(envelopeId, ct);
            if (hexKey is not null)
            {
                return hexKey;
            }
        }

        // Primeira vez ou envelopeId inválido: gera nova chave e armazena no vault.
        byte[] raw = RandomNumberGenerator.GetBytes(32);
        string newHexKey = Convert.ToHexString(raw).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(raw); // raw não é mais necessário

        string newEnvelopeId = await _vault.StoreSecretAsync(newHexKey, workspaceId, ct);

        // Persiste apenas o envelopeId (referência ao segredo, não o segredo em si).
        await File.WriteAllTextAsync(_keyRefPath, newEnvelopeId, ct);

        return newHexKey;
    }
}
