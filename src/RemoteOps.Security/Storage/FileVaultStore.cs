using System.Text.Json;

using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>
/// Store de referência em arquivo JSON, implementando envelopes e WDKs protegidas.
/// Útil para validar persistência entre reinícios. Guarda apenas ciphertext e
/// blobs DPAPI (base64) — nunca plaintext. A persistência de produção (SQLCipher)
/// pertence à frente feature/sync-local.
/// </summary>
public sealed class FileVaultStore : ICredentialStore, IWorkspaceKeyStore
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
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(root, JsonOptions);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class Root
    {
        public Dictionary<string, SecretEnvelope> Envelopes { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> WorkspaceKeys { get; init; } = new(StringComparer.Ordinal);
    }
}
