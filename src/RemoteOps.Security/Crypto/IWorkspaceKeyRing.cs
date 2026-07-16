namespace RemoteOps.Security.Crypto;

/// <summary>
/// Fornece a Workspace Data Key (WDK) de cada workspace. Existem duas raízes: a legada
/// (<see cref="WorkspaceKeyRing"/> — WDK aleatória protegida por DPAPI, presa à máquina) e a do
/// E2EE (<c>AmkWorkspaceKeyRing</c> — WDK derivada da AMK portável).
/// </summary>
public interface IWorkspaceKeyRing
{
    /// <summary>
    /// Esquema de cripto que esta raiz produz; vai carimbado em <c>SecretEnvelope.Algorithm</c>
    /// (ver <see cref="Vault.VaultAlgorithms"/>). É a RAIZ que decide — e não uma constante do
    /// cifrador — para que o Algorithm de cada envelope diga a verdade sobre como ele foi selado
    /// enquanto o cofre migra de raiz. O <c>LocalVaultMigrator</c> usa esse carimbo como marcador
    /// de idempotência.
    /// </summary>
    string AlgorithmId { get; }

    /// <summary>
    /// Recupera a WDK do workspace; cria e persiste (protegida) na primeira vez.
    /// Lança se a chave existe mas não pode ser desprotegida (outro usuário/máquina).
    /// </summary>
    Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Recupera a WDK existente, ou <c>null</c> se o workspace ainda não tem uma. Ao contrário de
    /// <see cref="GetOrCreateWorkspaceKeyAsync"/>, NÃO cria nem persiste nada. A migração de raiz
    /// depende dessa distinção: criar uma WDK nova por baixo de segredos já selados mascararia a
    /// perda da chave original como se fosse um cofre vazio.
    /// </summary>
    Task<WorkspaceKey?> TryGetWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default);
}
