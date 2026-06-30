using System.IO;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Garante que <see cref="VaultTokenStore"/> faz round-trip dos tokens via vault e que o
/// arquivo <c>.tokenref</c> contém apenas o envelopeId — nunca o token em claro.
/// </summary>
public sealed class VaultTokenStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _tokenRefPath;
    private readonly FakeCredentialVault _vault = new();

    public VaultTokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "remoteops-tokenstore-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _tokenRefPath = Path.Combine(_dir, "auth-ws.tokenref");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }

    private VaultTokenStore Store() => new(_vault, "ws-1", _tokenRefPath);

    [Fact]
    public async Task Save_Then_Load_RoundTrips()
    {
        var tokens = new TokenSet(
            "access-xyz", "refresh-xyz", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await Store().SaveAsync(tokens);

        TokenSet? loaded = await Store().LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal("access-xyz", loaded!.AccessToken);
        Assert.Equal("refresh-xyz", loaded.RefreshToken);
        Assert.Equal(tokens.ExpiresAt, loaded.ExpiresAt);
    }

    [Fact]
    public async Task Load_Returns_Null_When_No_Tokens()
    {
        Assert.Null(await Store().LoadAsync());
    }

    [Fact]
    public async Task TokenRef_File_Contains_Only_EnvelopeId_Not_Token()
    {
        await Store().SaveAsync(new TokenSet("ACCESS-SECRET", "REFRESH-SECRET", DateTimeOffset.UtcNow));

        string refContent = await File.ReadAllTextAsync(_tokenRefPath);

        Assert.DoesNotContain("ACCESS-SECRET", refContent);
        Assert.DoesNotContain("REFRESH-SECRET", refContent);
        Assert.InRange(refContent.Trim().Length, 1, 63);
    }

    [Fact]
    public async Task Save_Twice_Revokes_Previous_Envelope()
    {
        await Store().SaveAsync(new TokenSet("a1", "r1", DateTimeOffset.UtcNow));
        string firstEnvelopeId = (await File.ReadAllTextAsync(_tokenRefPath)).Trim();

        await Store().SaveAsync(new TokenSet("a2", "r2", DateTimeOffset.UtcNow));

        Assert.Null(await _vault.RetrieveSecretAsync(firstEnvelopeId));
        TokenSet? loaded = await Store().LoadAsync();
        Assert.Equal("a2", loaded!.AccessToken);
    }

    [Fact]
    public async Task Clear_Removes_Tokens_And_Ref()
    {
        await Store().SaveAsync(new TokenSet("a", "r", DateTimeOffset.UtcNow));

        await Store().ClearAsync();

        Assert.Null(await Store().LoadAsync());
        Assert.False(File.Exists(_tokenRefPath));
    }
}
