using RemoteOps.Security.Vault;
using RemoteOps.UnitTests.Security;

namespace RemoteOps.UnitTests.Terminal.Fakes;

/// <summary>
/// Vault em memória para testes de Terminal. Delega para VaultTestContext (real vault
/// com InMemory stores) para criar VaultSecrets legítimos sem reflection — o ctor de
/// VaultSecret é internal à Security, mas o vault real pode criá-lo.
/// </summary>
internal sealed class FakeVault : IVault
{
    private readonly VaultTestContext _ctx = VaultTestContext.InMemory();
    private static readonly VaultAccessContext TestCtx = new() { ActorUserId = "test-user" };

    /// <summary>
    /// Pré-armazena um segredo e devolve o envelopeId para usar em CredentialRef.SecretEnvelopeId.
    /// </summary>
    public async Task<string> SetupAsync(string secret, string credentialId = "cred1")
    {
        var envelope = await _ctx.Vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = "test-ws",
                CredentialId = credentialId,
                ActorUserId = "test",
            },
            secret.AsMemory());
        return envelope.EnvelopeId;
    }

    public Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext context, CancellationToken ct = default)
        => _ctx.Vault.RetrieveAsync(envelopeId, TestCtx, ct);

    public Task<SecretEnvelope> StoreAsync(VaultStoreRequest request, ReadOnlyMemory<char> secret, CancellationToken ct = default)
        => _ctx.Vault.StoreAsync(request, secret, ct);

    public Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> newSecret, VaultAccessContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task RevokeAsync(string envelopeId, VaultAccessContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}
