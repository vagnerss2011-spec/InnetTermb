using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Implementação em memória do <see cref="ILocalStore"/>. Usada em testes e no MVP sem sync.
/// </summary>
public sealed class InMemoryLocalStore : ILocalStore
{
    private readonly Dictionary<string, AssetGroup> _groups = [];
    private readonly Dictionary<string, Asset> _assets = [];
    private readonly Dictionary<string, Endpoint> _endpoints = [];
    private readonly Dictionary<string, CredentialRef> _credentialRefs = [];

    // Grupos ----------------------------------------------------------------

    public Task<IReadOnlyList<AssetGroup>> GetGroupsAsync(string workspaceId, CancellationToken ct = default)
    {
        IReadOnlyList<AssetGroup> result = _groups.Values
            .Where(g => g.WorkspaceId == workspaceId)
            .OrderBy(g => g.Name)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<AssetGroup> AddGroupAsync(string workspaceId, string name, string? parentId = null, CancellationToken ct = default)
    {
        var group = new AssetGroup
        {
            Id = NewId(),
            WorkspaceId = workspaceId,
            Name = name,
            ParentId = parentId,
        };
        _groups[group.Id] = group;
        return Task.FromResult(group);
    }

    public Task RenameGroupAsync(string id, string newName, CancellationToken ct = default)
    {
        if (_groups.TryGetValue(id, out AssetGroup? g))
        {
            g.Name = newName;
        }
        return Task.CompletedTask;
    }

    public Task DeleteGroupAsync(string id, CancellationToken ct = default)
    {
        _groups.Remove(id);
        return Task.CompletedTask;
    }

    // Ativos ----------------------------------------------------------------

    public Task<IReadOnlyList<Asset>> GetAssetsAsync(string workspaceId, string? groupId = null, CancellationToken ct = default)
    {
        IReadOnlyList<Asset> result = _assets.Values
            .Where(a => a.WorkspaceId == workspaceId)
            .Where(a => groupId == null || a.GroupId == groupId)
            .OrderBy(a => a.Name)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<Asset> AddAssetAsync(AddAssetRequest request, CancellationToken ct = default)
    {
        var asset = new Asset
        {
            Id = NewId(),
            WorkspaceId = request.WorkspaceId,
            GroupId = request.GroupId,
            Name = request.Name,
            Vendor = request.Vendor,
            Model = request.Model,
            Site = request.Site,
            Tags = [.. request.Tags],
        };
        _assets[asset.Id] = asset;
        return Task.FromResult(asset);
    }

    public Task<Asset> UpdateAssetAsync(Asset asset, CancellationToken ct = default)
    {
        _assets[asset.Id] = asset;
        return Task.FromResult(asset);
    }

    public Task DeleteAssetAsync(string id, CancellationToken ct = default)
    {
        _assets.Remove(id);
        foreach (string epId in _endpoints.Values.Where(e => e.AssetId == id).Select(e => e.Id).ToList())
        {
            _endpoints.Remove(epId);
        }
        return Task.CompletedTask;
    }

    public Task<Asset?> GetAssetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_assets.TryGetValue(id, out Asset? asset) ? asset : null);

    // Endpoints -------------------------------------------------------------

    public Task<Endpoint> AddEndpointAsync(Endpoint endpoint, CancellationToken ct = default)
    {
        _endpoints[endpoint.Id] = endpoint;
        // Refleti no asset
        if (_assets.TryGetValue(endpoint.AssetId, out Asset? asset))
        {
            var updated = new Asset
            {
                Id = asset.Id,
                WorkspaceId = asset.WorkspaceId,
                GroupId = asset.GroupId,
                Name = asset.Name,
                Vendor = asset.Vendor,
                Model = asset.Model,
                Site = asset.Site,
                Tags = asset.Tags,
                Version = asset.Version,
                Endpoints = [.. asset.Endpoints, endpoint],
            };
            _assets[asset.Id] = updated;
        }
        return Task.FromResult(endpoint);
    }

    public Task DeleteEndpointAsync(string id, CancellationToken ct = default)
    {
        _endpoints.Remove(id);
        return Task.CompletedTask;
    }

    // Referências de credencial --------------------------------------------

    public Task<IReadOnlyList<CredentialRef>> GetCredentialRefsAsync(string workspaceId, CancellationToken ct = default)
    {
        IReadOnlyList<CredentialRef> result = _credentialRefs.Values
            .Where(c => c.Scope == workspaceId || c.Scope == null)
            .OrderBy(c => c.Name)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<CredentialRef> AddCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
    {
        _credentialRefs[credentialRef.Id] = credentialRef;
        return Task.FromResult(credentialRef);
    }

    public Task DeleteCredentialRefAsync(string id, CancellationToken ct = default)
    {
        _credentialRefs.Remove(id);
        return Task.CompletedTask;
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}
