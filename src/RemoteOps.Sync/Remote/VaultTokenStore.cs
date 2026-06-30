using System.Text.Json;

using RemoteOps.Security;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// <see cref="ITokenStore"/> que guarda os tokens como UM segredo no vault (DPAPI/envelope,
/// ADR-003) e persiste apenas o <c>envelopeId</c> num arquivo <c>*.tokenref</c> — nunca o token
/// em claro. Rotação (novo save) revoga o envelope anterior.
/// </summary>
public sealed class VaultTokenStore : ITokenStore
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly ICredentialVault _vault;
    private readonly string _workspaceId;
    private readonly string _tokenRefPath;

    public VaultTokenStore(ICredentialVault vault, string workspaceId, string tokenRefPath)
    {
        _vault = vault;
        _workspaceId = workspaceId;
        _tokenRefPath = tokenRefPath;
    }

    public async Task<TokenSet?> LoadAsync(CancellationToken ct = default)
    {
        string? envelopeId = await ReadRefAsync(ct);
        if (string.IsNullOrEmpty(envelopeId))
        {
            return null;
        }

        string? json = await _vault.RetrieveSecretAsync(envelopeId, ct);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<TokenSet>(json, s_json);
    }

    public async Task SaveAsync(TokenSet tokens, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(tokens, s_json);
        string newEnvelopeId = await _vault.StoreSecretAsync(json, _workspaceId, ct);

        string? oldEnvelopeId = await ReadRefAsync(ct);
        await WriteRefAsync(newEnvelopeId, ct);

        // Revogação best-effort: se o processo encerrar entre o WriteRefAsync e o revoke, o envelope
        // antigo fica órfão no vault local até a próxima rotação. Não é explorável sem acesso DPAPI;
        // aceito como tradeoff (ADR-013). O .tokenref já aponta para o novo envelope (consistente).
        if (!string.IsNullOrEmpty(oldEnvelopeId) &&
            !string.Equals(oldEnvelopeId, newEnvelopeId, StringComparison.Ordinal))
        {
            await _vault.RevokeSecretAsync(oldEnvelopeId, ct);
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        string? envelopeId = await ReadRefAsync(ct);
        if (!string.IsNullOrEmpty(envelopeId))
        {
            await _vault.RevokeSecretAsync(envelopeId, ct);
        }

        if (File.Exists(_tokenRefPath))
        {
            File.Delete(_tokenRefPath);
        }
    }

    private async Task<string?> ReadRefAsync(CancellationToken ct)
    {
        if (!File.Exists(_tokenRefPath))
        {
            return null;
        }

        string content = (await File.ReadAllTextAsync(_tokenRefPath, ct)).Trim();
        return content.Length == 0 ? null : content;
    }

    private async Task WriteRefAsync(string envelopeId, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_tokenRefPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(_tokenRefPath, envelopeId, ct);
    }
}
