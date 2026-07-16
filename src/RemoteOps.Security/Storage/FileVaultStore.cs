using System.Globalization;
using System.Text.Json;

using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>
/// Store de referência em arquivo JSON, implementando envelopes e WDKs protegidas.
/// Útil para validar persistência entre reinícios. Guarda apenas ciphertext e
/// blobs DPAPI (base64) — nunca plaintext. A persistência de produção (SQLCipher)
/// pertence à frente feature/sync-local.
/// </summary>
public sealed class FileVaultStore : IVaultMigrationStore, IWorkspaceKeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _sync = new();

    public FileVaultStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public Task SaveAsync(SecretEnvelope envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_sync)
        {
            Root root = Load();
            root.Envelopes[envelope.EnvelopeId] = envelope;
            Persist(root);
        }

        return Task.CompletedTask;
    }

    public Task<SecretEnvelope?> GetAsync(string envelopeId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Root root = Load();
            root.Envelopes.TryGetValue(envelopeId, out SecretEnvelope? envelope);
            return Task.FromResult(envelope);
        }
    }

    public Task DeleteAsync(string envelopeId, CancellationToken ct = default)
    {
        lock (_sync)
        {
            Root root = Load();
            if (root.Envelopes.Remove(envelopeId))
            {
                Persist(root);
            }
        }

        return Task.CompletedTask;
    }

    // ---- Suporte à migração de raiz de chave (IVaultMigrationStore) ----

    public Task<IReadOnlyList<SecretEnvelope>> ListEnvelopesAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        lock (_sync)
        {
            Root root = Load();
            IReadOnlyList<SecretEnvelope> envelopes = root.Envelopes.Values
                .Where(e => string.Equals(e.WorkspaceId, workspaceId, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult(envelopes);
        }
    }

    public Task<string> CreateBackupAsync(string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            // Serializa sob o MESMO lock das escritas: o backup nunca captura o arquivo pela
            // metade (copiar o arquivo por fora poderia pegar um File.Move em andamento).
            string json = JsonSerializer.Serialize(Load(), JsonOptions);
            string path = NextBackupPath(reason);
            EnsureDirectory();
            File.WriteAllText(path, json);
            return Task.FromResult(path);
        }
    }

    public Task<VaultKeyRooting?> LoadKeyRootingAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        lock (_sync)
        {
            Root root = Load();
            VaultKeyRooting? rooting = root.KeyRooting.TryGetValue(workspaceId, out int value)
                ? (VaultKeyRooting)value
                : null;
            return Task.FromResult(rooting);
        }
    }

    public Task SaveKeyRootingAsync(string workspaceId, VaultKeyRooting rooting, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        lock (_sync)
        {
            Root root = Load();
            root.KeyRooting[workspaceId] = (int)rooting;
            Persist(root);
        }

        return Task.CompletedTask;
    }

    Task<byte[]?> IWorkspaceKeyStore.LoadAsync(string workspaceId, CancellationToken ct)
    {
        lock (_sync)
        {
            Root root = Load();
            byte[]? blob = root.WorkspaceKeys.TryGetValue(workspaceId, out string? b64)
                ? Convert.FromBase64String(b64)
                : null;
            return Task.FromResult(blob);
        }
    }

    Task IWorkspaceKeyStore.SaveAsync(string workspaceId, byte[] protectedKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protectedKey);
        lock (_sync)
        {
            Root root = Load();
            root.WorkspaceKeys[workspaceId] = Convert.ToBase64String(protectedKey);
            Persist(root);
        }

        return Task.CompletedTask;
    }

    private Root Load()
    {
        if (!File.Exists(_path))
        {
            return new Root();
        }

        string json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Root();
        }

        return JsonSerializer.Deserialize<Root>(json, JsonOptions) ?? new Root();
    }

    private void Persist(Root root)
    {
        EnsureDirectory();
        string json = JsonSerializer.Serialize(root, JsonOptions);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private void EnsureDirectory()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string NextBackupPath(string reason)
    {
        string safeReason = string.Concat(reason.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-'));
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string prefix = $"{_path}.{safeReason}-{stamp}";

        // Dois backups no mesmo segundo (retomada após falha, testes) não podem se sobrescrever:
        // um backup antigo pode ser a única cópia boa do cofre.
        string candidate = $"{prefix}.bak";
        for (int i = 2; File.Exists(candidate); i++)
        {
            candidate = $"{prefix}-{i}.bak";
        }

        return candidate;
    }

    private sealed class Root
    {
        public Dictionary<string, SecretEnvelope> Envelopes { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> WorkspaceKeys { get; init; } = new(StringComparer.Ordinal);

        /// <summary>workspaceId → <see cref="VaultKeyRooting"/> como int (estável no JSON entre versões).</summary>
        public Dictionary<string, int> KeyRooting { get; init; } = new(StringComparer.Ordinal);
    }
}
