using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class StoreCredentialRefResolver : ICredentialRefResolver
{
    private readonly ILocalStore _store;

    public StoreCredentialRefResolver(ILocalStore store) => _store = store;

    public async Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default)
    {
        var credRef = await _store.GetCredentialRefAsync(credentialRefId, ct).ConfigureAwait(false);
        return credRef ?? throw new InvalidOperationException(
            $"CredentialRef '{credentialRefId}' não encontrado no store local.");
    }
}
