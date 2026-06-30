using RemoteOps.Contracts.Assets;

namespace RemoteOps.Terminal;

/// <summary>
/// Resolve um EndpointId (referência persistida) para os dados reais de conexão.
/// Implementado pelo Desktop/DI container usando o ILocalStore.
/// </summary>
public interface IEndpointResolver
{
    Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default);
}
