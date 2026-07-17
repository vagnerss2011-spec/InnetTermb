using System.Security.Cryptography;

using RemoteOps.Security.Crypto;
using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Account;

/// <summary>
/// Key ring rootado na AMK: a WDK deixa de ser um segredo aleatório guardado por máquina e passa a
/// ser DERIVADA (HKDF) da AMK portável. Consequência: não há blob de chave pra persistir nem pra
/// sincronizar, e qualquer device que desembrulhe a AMK (escrow por senha ou por chave de
/// recuperação) abre o cofre — que é justamente o bloqueador que a Fase 1 resolve.
/// Substitui o <see cref="WorkspaceKeyRing"/> (raiz DPAPI) depois que o
/// <see cref="LocalVaultMigrator"/> troca a raiz do cofre local. Ver spec §4.1.
/// </summary>
public sealed class AmkWorkspaceKeyRing : IWorkspaceKeyRing, IDisposable
{
    private const int AmkSize = 32;

    private readonly byte[] _amk;
    private bool _disposed;

    /// <summary>Guarda uma CÓPIA da AMK (zerada no <see cref="Dispose"/>) — o chamador segue dono da dele.</summary>
    public AmkWorkspaceKeyRing(ReadOnlySpan<byte> amk)
    {
        if (amk.Length != AmkSize)
        {
            throw new ArgumentException($"A AMK precisa ter {AmkSize} bytes (recebidos {amk.Length}).", nameof(amk));
        }

        _amk = amk.ToArray();
    }

    public string AlgorithmId => VaultAlgorithms.AmkRootedV1;

    public Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // Determinística: não há estado pra criar nem corrida pra proteger — "GetOrCreate" e
        // "TryGet" colapsam no mesmo derive, e nunca devolvem null.
        return Task.FromResult(new WorkspaceKey(AmkKeyDerivation.DeriveWorkspaceKey(_amk, workspaceId)));
    }

    public async Task<WorkspaceKey?> TryGetWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default) =>
        await GetOrCreateWorkspaceKeyAsync(workspaceId, ct).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_amk);
        _disposed = true;
    }
}
