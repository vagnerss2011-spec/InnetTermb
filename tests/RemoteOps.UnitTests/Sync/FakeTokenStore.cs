using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

/// <summary>Token store em memória para testes; conta os saves para verificar refresh.</summary>
internal sealed class FakeTokenStore : ITokenStore
{
    private TokenSet? _tokens;

    public FakeTokenStore(TokenSet? initial = null)
    {
        _tokens = initial;
    }

    public int SaveCount { get; private set; }

    public TokenSet? Current => _tokens;

    public Task<TokenSet?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_tokens);

    public Task SaveAsync(TokenSet tokens, CancellationToken ct = default)
    {
        _tokens = tokens;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _tokens = null;
        return Task.CompletedTask;
    }
}
