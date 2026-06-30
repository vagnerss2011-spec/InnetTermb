using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

public sealed class VaultRoundTripTests
{
    private const string Workspace = "ws-01";

    [Fact]
    public async Task Store_Then_Retrieve_Returns_Original_Secret()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();
        const string secret = "S3nh@-Admin-#2026!"; // pragma: allowlist secret (fixture sintética)

        SecretEnvelope envelope = await ctx.Vault.StoreAsync(NewRequest(), secret.AsMemory());
        using VaultSecret revealed = await ctx.Vault.RetrieveAsync(envelope.EnvelopeId, Access());

        Assert.Equal(secret, revealed.RevealString());
        Assert.Equal(1, envelope.Version);
        Assert.Null(envelope.RevokedAt);
    }

    [Fact]
    public async Task Stored_Envelope_Never_Contains_Plaintext()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();
        const string secret = "PLAINTEXT-CANARY-9f3a"; // pragma: allowlist secret (fixture sintética)

        SecretEnvelope envelope = await ctx.Vault.StoreAsync(NewRequest(), secret.AsMemory());

        // Serialização completa do envelope não pode revelar o segredo.
        string json = JsonSerializer.Serialize(envelope);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);

        // Nem os bytes UTF-8 do segredo podem aparecer no ciphertext.
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        Assert.False(ContainsSequence(envelope.Ciphertext, secretBytes));
    }

    [Fact]
    public async Task Tampered_Ciphertext_Fails_Authentication()
    {
        var store = new InMemoryCredentialStore();
        VaultTestContext ctx = VaultTestContextOver(store);

        SecretEnvelope envelope = await ctx.Vault.StoreAsync(NewRequest(), "tamper-me".AsMemory());

        byte[] mutated = (byte[])envelope.Ciphertext.Clone();
        mutated[0] ^= 0xFF;
        await store.SaveAsync(envelope with { Ciphertext = mutated });

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => ctx.Vault.RetrieveAsync(envelope.EnvelopeId, Access()));
    }

    [Fact]
    public async Task Empty_Secret_RoundTrips()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();

        SecretEnvelope envelope = await ctx.Vault.StoreAsync(NewRequest(), string.Empty.AsMemory());
        using VaultSecret revealed = await ctx.Vault.RetrieveAsync(envelope.EnvelopeId, Access());

        Assert.Equal(string.Empty, revealed.RevealString());
    }

    private static VaultTestContext VaultTestContextOver(InMemoryCredentialStore store)
    {
        var keyStore = new InMemoryWorkspaceKeyStore();
        var keyRing = new RemoteOps.Security.Crypto.WorkspaceKeyRing(keyStore, new FakeKeyProtector("userA@machine1"));
        var audit = new RemoteOps.Security.Audit.InMemoryVaultAuditSink();
        return new VaultTestContext
        {
            Vault = new CredentialVault(store, keyRing, audit),
            Audit = audit,
        };
    }

    private static VaultStoreRequest NewRequest() => new()
    {
        WorkspaceId = Workspace,
        CredentialId = "cred-01",
        Type = "password",
        ActorUserId = "operator-1",
    };

    private static VaultAccessContext Access() => new() { ActorUserId = "operator-1" };

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
