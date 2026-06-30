using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Integration;

internal sealed class LocalStoreRdpEndpointResolver : IRdpEndpointResolver
{
    private readonly ILocalStore _store;

    public LocalStoreRdpEndpointResolver(ILocalStore store) => _store = store;

    public async Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default)
    {
        var endpoint = await _store.GetEndpointAsync(endpointId, ct).ConfigureAwait(false);
        return endpoint ?? throw new InvalidOperationException(
            $"Endpoint '{endpointId}' não encontrado no store local.");
    }
}
