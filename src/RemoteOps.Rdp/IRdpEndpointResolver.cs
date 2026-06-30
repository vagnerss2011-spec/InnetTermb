using RemoteOps.Contracts.Assets;

namespace RemoteOps.Rdp;

/// <summary>Resolve um EndpointId para os dados reais de conexão. Implementado pelo Desktop.</summary>
public interface IRdpEndpointResolver
{
    Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default);
}
