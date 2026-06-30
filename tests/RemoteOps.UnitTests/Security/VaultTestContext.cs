using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// Monta um <see cref="CredentialVault"/> para testes, com stores e protetor
/// injetáveis. Padrão: stores em memória + protetor "fake" determinístico.
/// </summary>
internal sealed class VaultTestContext
{
    public required CredentialVault Vault { get; init; }

    public required InMemoryVaultAuditSink Audit { get; init; }

    public static VaultTestContext InMemory(string identity = "userA@machine1")
    {
        var store = new InMemoryCredentialStore();
        var keyStore = new InMemoryWorkspaceKeyStore();
        return Build(store, keyStore, new FakeKeyProtector(identity));
    }

    /// <summary>Constrói um vault sobre um <see cref="FileVaultStore"/> compartilhado.</summary>
    public static VaultTestContext OverFile(FileVaultStore file, string identity)
    {
        return Build(file, file, new FakeKeyProtector(identity));
    }

    private static VaultTestContext Build(ICredentialStore store, IWorkspaceKeyStore keyStore, ILocalKeyProtector protector)
    {
        var audit = new InMemoryVaultAuditSink();
        var keyRing = new WorkspaceKeyRing(keyStore, protector);
        var vault = new CredentialVault(store, keyRing, audit);
        return new VaultTestContext { Vault = vault, Audit = audit };
    }
}
