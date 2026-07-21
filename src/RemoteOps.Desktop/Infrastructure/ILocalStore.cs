using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Armazenamento local de metadados (grupos, ativos, endpoints, refs de credencial).
/// Nunca armazena segredos — apenas referências (SecretEnvelopeId).
/// </summary>
public interface ILocalStore
{
    // Grupos
    Task<IReadOnlyList<AssetGroup>> GetGroupsAsync(string workspaceId, CancellationToken ct = default);
    Task<AssetGroup> AddGroupAsync(string workspaceId, string name, string? parentId = null, CancellationToken ct = default);
    Task RenameGroupAsync(string id, string newName, CancellationToken ct = default);

    /// <summary>
    /// Regrava a linha INTEIRA do grupo e emite o patch COMPLETO no outbox.
    ///
    /// <para><b>Por que existe além do <see cref="RenameGroupAsync"/>:</b> o rename empurra um patch
    /// PARCIAL (só <c>name</c>), o que é correto para renomear e inútil para reparar. O reenvio do
    /// acervo (<see cref="CloudResyncService"/>) precisa subir o grupo inteiro — inclusive
    /// <c>parent_id</c> e <c>default_credential_ref_id</c> —, senão um grupo que chegou incompleto no
    /// outro device continua incompleto para sempre. Os outros três tipos já tinham o seu
    /// <c>Update*Async</c>; o grupo era o único sem.</para>
    /// </summary>
    Task<AssetGroup> UpdateGroupAsync(AssetGroup group, CancellationToken ct = default);

    Task DeleteGroupAsync(string id, CancellationToken ct = default);

    // Ativos (hosts)
    Task<IReadOnlyList<Asset>> GetAssetsAsync(string workspaceId, string? groupId = null, CancellationToken ct = default);
    Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default);
    Task<Asset> AddAssetAsync(AddAssetRequest request, CancellationToken ct = default);
    Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default);
    Task DeleteAssetAsync(string id, CancellationToken ct = default);

    // Endpoints
    Task<Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct = default);
    Task<Endpoint> AddEndpointAsync(Endpoint endpoint, CancellationToken ct = default);

    /// <summary>Atualiza um endpoint existente (mesmo Id) — ex.: o perfil (Backspace, SSH).</summary>
    Task<Endpoint> UpdateEndpointAsync(Endpoint endpoint, CancellationToken ct = default);

    Task DeleteEndpointAsync(string id, CancellationToken ct = default);

    // Referências de credencial (metadados — sem segredo)
    Task<IReadOnlyList<CredentialRef>> GetCredentialRefsAsync(string workspaceId, CancellationToken ct = default);
    Task<CredentialRef?> GetCredentialRefAsync(string credentialRefId, CancellationToken ct = default);
    Task<CredentialRef> AddCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default);
    Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default);
    Task DeleteCredentialRefAsync(string id, CancellationToken ct = default);
}
