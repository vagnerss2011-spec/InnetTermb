using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class LocalStoreEndpointResolver : IEndpointResolver
{
    private readonly ILocalStore _store;

    public LocalStoreEndpointResolver(ILocalStore store) => _store = store;

    public async Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default)
    {
        var endpoint = await _store.GetEndpointAsync(endpointId, ct).ConfigureAwait(false);
        return endpoint ?? throw new InvalidOperationException(
            $"Endpoint '{endpointId}' não encontrado no store local.");
    }
}
