using RemoteOps.Contracts.Assets;
using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class InMemoryEndpointResolver : IEndpointResolver
{
    private readonly Dictionary<string, Endpoint> _endpoints = [];

    public void Add(Endpoint endpoint) => _endpoints[endpoint.Id] = endpoint;

    public Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default)
        => _endpoints.TryGetValue(endpointId, out var ep)
            ? Task.FromResult(ep)
            : Task.FromException<Endpoint>(new KeyNotFoundException($"Endpoint '{endpointId}' não encontrado."));
}
