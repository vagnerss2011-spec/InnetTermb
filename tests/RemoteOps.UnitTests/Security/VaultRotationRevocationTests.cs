using RemoteOps.Security;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

public sealed class VaultRotationRevocationTests
{
    [Fact]
    public async Task Rotate_Bumps_Version_New_Secret_Retrievable_Old_Revoked()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();

        SecretEnvelope original = await ctx.Vault.StoreAsync(Request(), "old-secret".AsMemory());
        SecretEnvelope rotated = await ctx.Vault.RotateAsync(original.EnvelopeId, "new-secret".AsMemory(), Access());

        Assert.Equal(original.Version + 1, rotated.Version);
        Assert.Equal(original.CredentialId, rotated.CredentialId);
        Assert.NotEqual(original.EnvelopeId, rotated.EnvelopeId);

        using VaultSecret current = await ctx.Vault.RetrieveAsync(rotated.EnvelopeId, Access());
        Assert.Equal("new-secret", current.RevealString());

        // O envelope antigo foi revogado (não pode mais ser aberto).
        await Assert.ThrowsAsync<VaultException>(
            () => ctx.Vault.RetrieveAsync(original.EnvelopeId, Access()));
    }

    [Fact]
    public async Task Revoke_Makes_Secret_Unretrievable()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();
        SecretEnvelope envelope = await ctx.Vault.StoreAsync(Request(), "to-revoke".AsMemory());

        await ctx.Vault.RevokeAsync(envelope.EnvelopeId, Access());

        await Assert.ThrowsAsync<VaultException>(
            () => ctx.Vault.RetrieveAsync(envelope.EnvelopeId, Access()));
    }

    [Fact]
    public async Task Revoking_Unknown_Envelope_Throws()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();

        await Assert.ThrowsAsync<VaultException>(
            () => ctx.Vault.RevokeAsync("does-not-exist", Access()));
    }

    [Fact]
    public async Task Thin_Contract_RoundTrips_And_Revokes()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();
        ICredentialVault thin = ctx.Vault;

        string envelopeId = await thin.StoreSecretAsync("contract-secret", "ws-01");
        string? revealed = await thin.RetrieveSecretAsync(envelopeId);
        Assert.Equal("contract-secret", revealed);

        await thin.RevokeSecretAsync(envelopeId);
        await Assert.ThrowsAsync<VaultException>(() => thin.RetrieveSecretAsync(envelopeId));
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
