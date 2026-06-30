namespace RemoteOps.Security.Storage;

/// <summary>
/// Persistência das Workspace Data Keys já protegidas localmente (blob DPAPI).
/// Nunca armazena chave em texto puro — somente o blob protegido pelo SO.
/// </summary>
public interface IWorkspaceKeyStore
{
    Task<byte[]?> LoadAsync(string workspaceId, CancellationToken ct = default);

    Task SaveAsync(string workspaceId, byte[] protectedKey, CancellationToken ct = default);
}
