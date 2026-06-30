using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Integration;

internal sealed class LocalStoreRdpCredentialRefResolver : IRdpCredentialRefResolver
{
    private readonly ILocalStore _store;

    public LocalStoreRdpCredentialRefResolver(ILocalStore store) => _store = store;

    public async Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default)
    {
        var credRef = await _store.GetCredentialRefAsync(credentialRefId, ct).ConfigureAwait(false);
        return credRef ?? throw new InvalidOperationException(
            $"CredentialRef '{credentialRefId}' não encontrada no store local.");
    }
}
