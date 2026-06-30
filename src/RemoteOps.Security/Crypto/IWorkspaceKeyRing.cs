namespace RemoteOps.Security.Crypto;

/// <summary>
/// Gera e recupera a Workspace Data Key (WDK) de cada workspace, mantendo a
/// cópia local protegida por <see cref="ILocalKeyProtector"/> (DPAPI no Windows).
/// </summary>
public interface IWorkspaceKeyRing
{
    /// <summary>
    /// Recupera a WDK do workspace; cria e persiste (protegida) na primeira vez.
    /// Lança se a chave existe mas não pode ser desprotegida (outro usuário/máquina).
    /// </summary>
    Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default);
}
