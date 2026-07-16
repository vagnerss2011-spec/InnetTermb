using System.IO;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Decorador de cofre que falha ao GRAVAR a partir da N-ésima chamada. Serve pra encenar a queda no
/// meio de uma página do pull — disco cheio, processo morto, permissão negada —, que é o caso em que
/// a ORDEM entre gravar o envelope e avançar o cursor decide se um segredo se perde ou não.
/// </summary>
internal sealed class FlakyEnvelopeStore : IVaultMigrationStore
{
    private readonly IVaultMigrationStore _inner;
    private int _saves;

    public FlakyEnvelopeStore(IVaultMigrationStore inner) => _inner = inner;

    /// <summary>A partir de quantas gravações estourar. <c>int.MaxValue</c> = nunca (default).</summary>
    public int FailSaveAfter { get; set; } = int.MaxValue;

    public Task SaveAsync(SecretEnvelope envelope, CancellationToken ct = default)
    {
        if (++_saves > FailSaveAfter)
        {
            throw new IOException("falha simulada ao gravar no cofre");
        }

        return _inner.SaveAsync(envelope, ct);
    }

    public Task<SecretEnvelope?> GetAsync(string envelopeId, CancellationToken ct = default)
        => _inner.GetAsync(envelopeId, ct);

    public Task DeleteAsync(string envelopeId, CancellationToken ct = default)
        => _inner.DeleteAsync(envelopeId, ct);

    public Task<IReadOnlyList<SecretEnvelope>> ListEnvelopesAsync(
        string workspaceId, CancellationToken ct = default)
        => _inner.ListEnvelopesAsync(workspaceId, ct);

    public Task<string> CreateBackupAsync(string reason, CancellationToken ct = default)
        => _inner.CreateBackupAsync(reason, ct);

    public Task<VaultKeyRooting?> LoadKeyRootingAsync(string workspaceId, CancellationToken ct = default)
        => _inner.LoadKeyRootingAsync(workspaceId, ct);

    public Task SaveKeyRootingAsync(
        string workspaceId, VaultKeyRooting rooting, CancellationToken ct = default)
        => _inner.SaveKeyRootingAsync(workspaceId, rooting, ct);
}
