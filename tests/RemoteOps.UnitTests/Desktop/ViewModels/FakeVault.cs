using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Security.Vault;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>Fake mínimo de IVault para testes de fiação (não faz criptografia real).</summary>
public sealed class FakeVault : IVault
{
    // Espelha o tombstone do cofre real: uma vez rotacionado/revogado, o envelope morre e
    // qualquer novo acesso a ele explode (CredentialVault.RequireActiveAsync).
    private readonly HashSet<string> _tombstoned = [];
    private int _rotations;

    public List<string> StoredCredentialIds { get; } = [];

    /// <summary>Envelopes que ENTRARAM na rotação (os antigos, que viram tombstone).</summary>
    public List<string> RotatedEnvelopeIds { get; } = [];

    /// <summary>Envelopes que SAÍRAM da rotação (os novos, vivos), na mesma ordem.</summary>
    public List<string> RotatedIntoEnvelopeIds { get; } = [];

    public List<string?> RevokedEnvelopeIds { get; } = [];

    public Task<SecretEnvelope> StoreAsync(VaultStoreRequest r, ReadOnlyMemory<char> secret, CancellationToken ct = default)
    {
        StoredCredentialIds.Add(r.CredentialId);
        return Task.FromResult(Env("env-" + r.CredentialId, r.CredentialId, r.WorkspaceId));
    }

    public Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default)
    {
        // O cofre real (CredentialVault.cs:88-102) cria um envelope com ID NOVO e tombstoneia o
        // antigo. Um fake que devolvesse o MESMO id esconderia justamente o bug do CredentialRef
        // órfão — por isso aqui o id muda de verdade, e rotacionar um tombstone explode.
        if (_tombstoned.Contains(envelopeId))
        {
            throw new VaultException($"Envelope '{envelopeId}' está revogado.");
        }

        RotatedEnvelopeIds.Add(envelopeId);
        _tombstoned.Add(envelopeId);
        string rotatedId = $"env-rot{++_rotations}";
        RotatedIntoEnvelopeIds.Add(rotatedId);
        return Task.FromResult(Env(rotatedId, "c", "ws-local"));
    }

    public Task RevokeAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default)
    {
        RevokedEnvelopeIds.Add(envelopeId);
        _tombstoned.Add(envelopeId);
        return Task.CompletedTask;
    }

    private static SecretEnvelope Env(string id, string cid, string ws) => new()
    {
        EnvelopeId = id,
        WorkspaceId = ws,
        CredentialId = cid,
        Type = "password",
        Version = 1,
        Algorithm = "test",
        WrappedCek = [],
        CekNonce = [],
        CekTag = [],
        Ciphertext = [],
        Nonce = [],
        Tag = [],
        CreatedAt = default,
    };
}
