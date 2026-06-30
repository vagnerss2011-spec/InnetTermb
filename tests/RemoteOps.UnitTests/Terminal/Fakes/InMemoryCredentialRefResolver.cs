using RemoteOps.Contracts.Assets;
using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class InMemoryCredentialRefResolver : ICredentialRefResolver
{
    private readonly Dictionary<string, CredentialRef> _refs = [];

    public void Add(CredentialRef credRef) => _refs[credRef.Id] = credRef;

    public Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default)
        => _refs.TryGetValue(credentialRefId, out var cr)
            ? Task.FromResult(cr)
            : Task.FromException<CredentialRef>(new KeyNotFoundException($"CredentialRef '{credentialRefId}' não encontrada."));
}
