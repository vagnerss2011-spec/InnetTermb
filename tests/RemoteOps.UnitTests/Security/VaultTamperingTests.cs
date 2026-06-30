using System.Security.Cryptography;

using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// Regressões sobre o material persistido: o AAD deve autenticar a identidade
/// estrutural do envelope (workspace, versão, tipo) e o tombstone deve apagar
/// o material criptográfico, não apenas marcar a data de revogação.
/// </summary>
public sealed class VaultTamperingTests
{
    [Fact]
    public async Task Revoked_Envelope_Has_Crypto_Material_Erased()
    {
        var store = new InMemoryCredentialStore();
        CredentialVault vault = BuildVault(store);

        SecretEnvelope envelope = await vault.StoreAsync(Request(), "to-erase".AsMemory());
        await vault.RevokeAsync(envelope.EnvelopeId, Access());

        SecretEnvelope? stored = await store.GetAsync(envelope.EnvelopeId);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.RevokedAt);
        // O material não pode sobreviver à revogação.
        Assert.Empty(stored.WrappedCek);
        Assert.Empty(stored.CekNonce);
        Assert.Empty(stored.CekTag);
        Assert.Empty(stored.Ciphertext);
        Assert.Empty(stored.Nonce);
        Assert.Empty(stored.Tag);
    }

    [Fact]
    public async Task Tampering_WorkspaceId_Breaks_Open()
    {
        await AssertTamperFails(e => e with { WorkspaceId = "ws-evil" });
    }

    [Fact]
    public async Task Tampering_Version_Breaks_Open()
    {
        await AssertTamperFails(e => e with { Version = e.Version + 1 });
    }

    [Fact]
    public async Task Tampering_Type_Breaks_Open()
    {
        // Garante que o Type entra no AAD (anti-escalada por troca de tipo).
        await AssertTamperFails(e => e with { Type = "admin" });
    }

    private static async Task AssertTamperFails(Func<SecretEnvelope, SecretEnvelope> tamper)
    {
        var store = new InMemoryCredentialStore();
        CredentialVault vault = BuildVault(store);

        SecretEnvelope envelope = await vault.StoreAsync(Request(), "aad-bound".AsMemory());
        // Mantém o mesmo EnvelopeId (chave do store) mas adultera um campo do AAD.
        await store.SaveAsync(tamper(envelope) with { EnvelopeId = envelope.EnvelopeId });

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => vault.RetrieveAsync(envelope.EnvelopeId, Access()));
    }

    private static CredentialVault BuildVault(ICredentialStore store)
    {
        var keyRing = new WorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), new FakeKeyProtector("userA@machine1"));
        return new CredentialVault(store, keyRing, new InMemoryVaultAuditSink());
    }

    private static VaultStoreRequest Request() => new()
    {
        WorkspaceId = "ws-01",
        CredentialId = "cred-01",
        Type = "password",
        ActorUserId = "operator-1",
    };

    private static VaultAccessContext Access() => new() { ActorUserId = "operator-1" };
}
