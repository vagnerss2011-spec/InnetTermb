using System.Text.Json;

using RemoteOps.Security.Audit;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

public sealed class VaultAuditTests
{
    private const string Secret = "AUDIT-CANARY-7Kx"; // pragma: allowlist secret (fixture sintética)

    [Fact]
    public async Task Audit_Records_Lifecycle_Without_Any_Secret()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();

        SecretEnvelope created = await ctx.Vault.StoreAsync(Request(), Secret.AsMemory());
        using (await ctx.Vault.RetrieveAsync(created.EnvelopeId, Access()))
        {
        }

        SecretEnvelope rotated = await ctx.Vault.RotateAsync(created.EnvelopeId, "rotated-secret".AsMemory(), Access());
        await ctx.Vault.RevokeAsync(rotated.EnvelopeId, Access());

        string[] actions = ctx.Audit.Events.Select(e => e.Action).ToArray();
        Assert.Contains(VaultAction.CredentialCreate, actions);
        Assert.Contains(VaultAction.CredentialUse, actions);
        Assert.Contains(VaultAction.CredentialRotate, actions);
        Assert.Contains(VaultAction.CredentialRevoke, actions);

        // Nenhum evento (serializado ou via ToString) pode conter o segredo.
        foreach (VaultAuditEvent auditEvent in ctx.Audit.Events)
        {
            string json = JsonSerializer.Serialize(auditEvent);
            Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
            Assert.DoesNotContain("rotated-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, auditEvent.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task VaultSecret_ToString_Is_Redacted()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();
        SecretEnvelope envelope = await ctx.Vault.StoreAsync(Request(), Secret.AsMemory());

        using VaultSecret revealed = await ctx.Vault.RetrieveAsync(envelope.EnvelopeId, Access());
        Assert.DoesNotContain(Secret, revealed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VaultSecret_Throws_After_Dispose()
    {
        VaultTestContext ctx = VaultTestContext.InMemory();
        SecretEnvelope envelope = await ctx.Vault.StoreAsync(Request(), Secret.AsMemory());

        VaultSecret revealed = await ctx.Vault.RetrieveAsync(envelope.EnvelopeId, Access());
        revealed.Dispose();

        Assert.Throws<ObjectDisposedException>(() => revealed.RevealString());
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
