using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Update;

/// <summary>
/// Feed de política nulo: sem versão mínima exigida (nenhum update forçado). Usado quando
/// REMOTEOPS_UPDATE_POLICY_URL não está configurada, para que o update VOLUNTÁRIO funcione
/// sem depender de um feed de política.
/// </summary>
public sealed class NoPolicyFeedSource : IUpdatePolicyFeedSource
{
    public Task<AppVersion?> GetMinimumRequiredVersionAsync(CancellationToken ct = default)
        => Task.FromResult<AppVersion?>(null);
}
