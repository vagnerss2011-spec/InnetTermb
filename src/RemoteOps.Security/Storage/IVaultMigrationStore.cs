using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>
/// O que a migração de raiz de chave (<c>LocalVaultMigrator</c>) precisa do store, além do CRUD de
/// envelopes: enumerar o workspace, tirar um snapshot antes de reescrever, e registrar em que raiz
/// o cofre está (herdado de <see cref="IVaultRootingStore"/>).
///
/// É uma interface OPT-IN (e não métodos novos no <see cref="ICredentialStore"/>) de propósito: o
/// store durável de produção (SQLCipher) é de outra frente, e quem não precisa migrar não deve ser
/// obrigado a implementar backup nem versionamento de raiz.
///
/// <para><b>Por que o marcador de raiz saiu daqui para o <see cref="IVaultRootingStore"/>:</b> a
/// migração deixou de ser a única a escrevê-lo. A raiz do TIME grava o marcador na mesma operação em
/// que a WK aterrissa, e obrigá-la a depender de backup e enumeração de envelopes só para gravar um
/// inteiro seria acoplamento sem contrapartida. Refactor puro — nenhum implementador muda.</para>
/// </summary>
public interface IVaultMigrationStore : ICredentialStore, IVaultRootingStore
{
    /// <summary>Todos os envelopes do workspace, incluindo revogados (tombstones).</summary>
    Task<IReadOnlyList<SecretEnvelope>> ListEnvelopesAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Snapshot íntegro do estado atual, antes de uma reescrita destrutiva. Retorna o
    /// identificador do backup (para o <see cref="FileVaultStore"/>, o caminho do arquivo).
    /// </summary>
    Task<string> CreateBackupAsync(string reason, CancellationToken ct = default);
}
