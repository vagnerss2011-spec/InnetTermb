using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>
/// O que a migração de raiz de chave (<c>LocalVaultMigrator</c>) precisa do store, além do CRUD de
/// envelopes: enumerar o workspace, tirar um snapshot antes de reescrever, e registrar em que raiz
/// o cofre está.
///
/// É uma interface OPT-IN (e não métodos novos no <see cref="ICredentialStore"/>) de propósito: o
/// store durável de produção (SQLCipher) é de outra frente, e quem não precisa migrar não deve ser
/// obrigado a implementar backup nem versionamento de raiz.
/// </summary>
public interface IVaultMigrationStore : ICredentialStore
{
    /// <summary>Todos os envelopes do workspace, incluindo revogados (tombstones).</summary>
    Task<IReadOnlyList<SecretEnvelope>> ListEnvelopesAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Snapshot íntegro do estado atual, antes de uma reescrita destrutiva. Retorna o
    /// identificador do backup (para o <see cref="FileVaultStore"/>, o caminho do arquivo).
    /// </summary>
    Task<string> CreateBackupAsync(string reason, CancellationToken ct = default);

    /// <summary>Raiz registrada para o workspace, ou <c>null</c> se nunca foi migrado.</summary>
    Task<VaultKeyRooting?> LoadKeyRootingAsync(string workspaceId, CancellationToken ct = default);

    Task SaveKeyRootingAsync(string workspaceId, VaultKeyRooting rooting, CancellationToken ct = default);
}
