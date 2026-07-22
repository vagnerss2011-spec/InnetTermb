using System.Security.Cryptography;
using System.Text;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Crypto;

/// <summary>
/// Implementação padrão do key ring. A WDK (32 bytes) é gerada com CSPRNG,
/// protegida localmente (DPAPI) e persistida via <see cref="IWorkspaceKeyStore"/>.
/// A entropia DPAPI é ligada ao workspace, somando defesa contra troca de blob.
/// </summary>
public sealed class WorkspaceKeyRing : IWorkspaceKeyRing
{
    private const int WorkspaceKeySize = 32; // AES-256

    private readonly IWorkspaceKeyStore _store;
    private readonly ILocalKeyProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceKeyRing(IWorkspaceKeyStore store, ILocalKeyProtector protector)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        _store = store;
        _protector = protector;
    }

    public string AlgorithmId => VaultAlgorithms.DpapiRootedV1;

    public async Task<WorkspaceKey?> TryGetWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        byte[]? existing = await _store.LoadAsync(workspaceId, ct).ConfigureAwait(false);
        return existing is null ? null : Unprotect(workspaceId, existing);
    }

    public async Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        byte[]? existing = await _store.LoadAsync(workspaceId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return Unprotect(workspaceId, existing);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            existing = await _store.LoadAsync(workspaceId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return Unprotect(workspaceId, existing);
            }

            byte[] fresh = RandomNumberGenerator.GetBytes(WorkspaceKeySize);
            try
            {
                byte[] protectedBlob = _protector.Protect(fresh, Entropy(workspaceId));
                await _store.SaveAsync(workspaceId, protectedBlob, ct).ConfigureAwait(false);
                return new WorkspaceKey(fresh, VaultAlgorithms.DpapiRootedV1);
            }
            catch
            {
                // Se a proteção/persistência falhar, a WDK bruta não chega ao
                // WorkspaceKey (que a zeraria no Dispose): zere aqui para não deixar
                // material de chave órfão na heap.
                CryptographicOperations.ZeroMemory(fresh);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkspaceKey Unprotect(string workspaceId, byte[] protectedBlob)
    {
        byte[] key = _protector.Unprotect(protectedBlob, Entropy(workspaceId));
        return new WorkspaceKey(key, VaultAlgorithms.DpapiRootedV1);
    }

    private static byte[] Entropy(string workspaceId) =>
        Encoding.UTF8.GetBytes("remoteops:wdk:" + workspaceId);
}
