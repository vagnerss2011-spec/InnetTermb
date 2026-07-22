using System.Buffers;
using System.Security.Cryptography;
using System.Text;

using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;

namespace RemoteOps.Security.Vault;

/// <summary>
/// Implementação do cofre de credenciais. Orquestra envelope encryption
/// (<see cref="EnvelopeCipher"/>), a Workspace Data Key protegida por DPAPI
/// (<see cref="IWorkspaceKeyRing"/>), a persistência (<see cref="ICredentialStore"/>)
/// e a auditoria estruturada (<see cref="IVaultAuditSink"/>).
///
/// Garantias: nunca persiste/loga plaintext; o segredo só existe descriptografado
/// dentro do <see cref="VaultSecret"/> retornado, com lifetime mínimo.
/// </summary>
public sealed class CredentialVault : IVault, ICredentialVault
{
    private const string SystemActor = "system";

    private readonly ICredentialStore _store;
    private readonly IWorkspaceKeyRing _keyRing;
    private readonly IVaultAuditSink _audit;
    private readonly TimeProvider _clock;

    public CredentialVault(
        ICredentialStore store,
        IWorkspaceKeyRing keyRing,
        IVaultAuditSink audit,
        TimeProvider? clock = null)
    {
        // ADR-003: "recuperação exige auditoria". O sink é obrigatório de propósito —
        // um default silencioso (NullVaultAuditSink) transformaria a exigência de
        // auditoria em no-op numa configuração de DI incorreta. Quem realmente quiser
        // descartar eventos deve passar NullVaultAuditSink.Instance explicitamente.
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(audit);
        _store = store;
        _keyRing = keyRing;
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<SecretEnvelope> StoreAsync(VaultStoreRequest request, ReadOnlyMemory<char> secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CredentialId);

        using WorkspaceKey workspaceKey = await _keyRing.GetOrCreateWorkspaceKeyAsync(request.WorkspaceId, ct).ConfigureAwait(false);
        SecretEnvelope envelope = CreateEnvelope(
            request.WorkspaceId, request.CredentialId, request.Type, version: 1,
            workspaceKey.Key.Span, workspaceKey.AlgorithmId, secret);
        await _store.SaveAsync(envelope, ct).ConfigureAwait(false);
        await EmitAsync(VaultAction.CredentialCreate, envelope, request.ActorUserId, request.DeviceId, ct).ConfigureAwait(false);
        return envelope;
    }

    public async Task<VaultSecret> RetrieveAsync(string envelopeId, VaultAccessContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        SecretEnvelope envelope = await RequireActiveAsync(envelopeId, ct).ConfigureAwait(false);

        using WorkspaceKey workspaceKey = await _keyRing.GetOrCreateWorkspaceKeyAsync(envelope.WorkspaceId, ct).ConfigureAwait(false);
        byte[] plaintext = EnvelopeCipher.Open(
            workspaceKey.Key.Span,
            envelope,
            EnvelopeCipher.BuildAad(envelope),
            EnvelopeCipher.BuildWrapAad(envelope.WorkspaceId));

        // O plaintext só fica seguro (auto-zerável) dentro do VaultSecret. Até lá, se
        // qualquer await falhar (auditoria/cancelamento), zere o buffer manualmente
        // para não deixar segredo órfão na heap aguardando o GC.
        try
        {
            await EmitAsync(VaultAction.CredentialUse, envelope, context.ActorUserId, context.DeviceId, ct).ConfigureAwait(false);
            return new VaultSecret(plaintext);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    public async Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> newSecret, VaultAccessContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        SecretEnvelope current = await RequireActiveAsync(envelopeId, ct).ConfigureAwait(false);

        using WorkspaceKey workspaceKey = await _keyRing.GetOrCreateWorkspaceKeyAsync(current.WorkspaceId, ct).ConfigureAwait(false);
        SecretEnvelope rotated = CreateEnvelope(
            current.WorkspaceId, current.CredentialId, current.Type, current.Version + 1,
            workspaceKey.Key.Span, workspaceKey.AlgorithmId, newSecret);
        await _store.SaveAsync(rotated, ct).ConfigureAwait(false);
        SecretEnvelope tombstone = await TombstoneAsync(current, ct).ConfigureAwait(false);
        // A rotação revoga o envelope anterior: emite o revoke explícito para que uma
        // auditoria forense que filtre por credential.revoke encontre a revogação.
        await EmitAsync(VaultAction.CredentialRevoke, tombstone, context.ActorUserId, context.DeviceId, ct).ConfigureAwait(false);
        await EmitAsync(VaultAction.CredentialRotate, rotated, context.ActorUserId, context.DeviceId, ct).ConfigureAwait(false);
        return rotated;
    }

    public async Task RevokeAsync(string envelopeId, VaultAccessContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        SecretEnvelope current = await RequireActiveAsync(envelopeId, ct).ConfigureAwait(false);
        SecretEnvelope tombstone = await TombstoneAsync(current, ct).ConfigureAwait(false);
        await EmitAsync(VaultAction.CredentialRevoke, tombstone, context.ActorUserId, context.DeviceId, ct).ConfigureAwait(false);
    }

    // ---- Contrato fino cross-module (ICredentialVault) ----

    async Task<string> ICredentialVault.StoreSecretAsync(string secret, string workspaceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secret);
        SecretEnvelope envelope = await StoreAsync(
            new VaultStoreRequest { WorkspaceId = workspaceId, CredentialId = Guid.NewGuid().ToString("n"), ActorUserId = SystemActor },
            secret.AsMemory(),
            ct).ConfigureAwait(false);
        return envelope.EnvelopeId;
    }

    async Task<string?> ICredentialVault.RetrieveSecretAsync(string envelopeId, CancellationToken ct)
    {
        using VaultSecret secret = await RetrieveAsync(envelopeId, new VaultAccessContext { ActorUserId = SystemActor }, ct).ConfigureAwait(false);
        return secret.RevealString();
    }

    Task ICredentialVault.RevokeSecretAsync(string envelopeId, CancellationToken ct) =>
        RevokeAsync(envelopeId, new VaultAccessContext { ActorUserId = SystemActor }, ct);

    // ---- Internos ----

    private SecretEnvelope CreateEnvelope(
        string workspaceId,
        string credentialId,
        string type,
        int version,
        ReadOnlySpan<byte> workspaceKey,
        string algorithm,
        ReadOnlyMemory<char> secret)
    {
        string envelopeId = Guid.NewGuid().ToString("n");
        // O AAD depende do ESQUEMA, e o esquema chega JUNTO da chave (WorkspaceKey.AlgorithmId) — e
        // não numa segunda pergunta ao chaveiro. Com o cofre atendendo duas raízes ao mesmo tempo
        // (pessoal AMK + time WK), perguntar em separado permitiria selar com a chave de uma raiz e
        // declarar a outra: um envelope que mente sobre si mesmo, cujo estrago só aparece meses
        // depois. No WkRootedV1 o AAD prende também o credentialId; nas raízes antigas o formato
        // fica idêntico ao que já está em disco.
        byte[] aad = EnvelopeCipher.BuildAad(envelopeId, workspaceId, version, type, credentialId, algorithm);
        byte[] wrapAad = EnvelopeCipher.BuildWrapAad(workspaceId);

        int rentSize = Encoding.UTF8.GetMaxByteCount(Math.Max(1, secret.Length));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(rentSize);
        int written = 0;
        try
        {
            written = Encoding.UTF8.GetBytes(secret.Span, buffer);
            EnvelopeCipher.SealedSecret payload = EnvelopeCipher.Seal(workspaceKey, buffer.AsSpan(0, written), aad, wrapAad);
            return new SecretEnvelope
            {
                EnvelopeId = envelopeId,
                WorkspaceId = workspaceId,
                CredentialId = credentialId,
                Type = type,
                Version = version,
                // Quem carimba o esquema é a CHAVE que selou: assim o Algorithm de cada envelope diz
                // a verdade sobre como ele foi selado, inclusive num cofre que atende duas raízes ao
                // mesmo tempo e depois de o cofre pessoal migrar de DPAPI para AMK.
                Algorithm = algorithm,
                WrappedCek = payload.WrappedCek,
                CekNonce = payload.CekNonce,
                CekTag = payload.CekTag,
                Ciphertext = payload.Ciphertext,
                Nonce = payload.Nonce,
                Tag = payload.Tag,
                CreatedAt = _clock.GetUtcNow(),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, written));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<SecretEnvelope> RequireActiveAsync(string envelopeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeId);
        SecretEnvelope envelope = await _store.GetAsync(envelopeId, ct).ConfigureAwait(false)
            ?? throw new VaultException($"Envelope '{envelopeId}' não encontrado.");
        if (envelope.RevokedAt is not null)
        {
            throw new VaultException($"Envelope '{envelopeId}' está revogado.");
        }

        return envelope;
    }

    private async Task<SecretEnvelope> TombstoneAsync(SecretEnvelope envelope, CancellationToken ct)
    {
        SecretEnvelope tombstone = envelope with
        {
            // A revogação é uma VERSÃO NOVA do registro, não uma edição da atual — e é isso que a
            // faz sair deste PC. Nascendo com a mesma versão do envelope vivo, o ledger de push a
            // pularia ("o servidor já tem esta versão") e o servidor a recusaria por monotonicidade:
            // a lápide morreria aqui e o envelope antigo continuaria vivo e DECIFRÁVEL no disco do
            // outro device, para sempre. Incrementar acontece no máximo uma vez por envelope
            // (RequireActiveAsync recusa revogar duas vezes), então não há corrida de versão.
            //
            // Seguro para o AAD: o material vai zerado logo abaixo e um tombstone nunca é aberto
            // (RequireActiveAsync barra antes), então a versão no AAD nunca é conferida de novo.
            Version = envelope.Version + 1,
            RevokedAt = _clock.GetUtcNow(),
            WrappedCek = [],
            CekNonce = [],
            CekTag = [],
            Ciphertext = [],
            Nonce = [],
            Tag = [],
        };
        await _store.SaveAsync(tombstone, ct).ConfigureAwait(false);
        return tombstone;
    }

    private Task EmitAsync(string action, SecretEnvelope envelope, string actorUserId, string? deviceId, CancellationToken ct) =>
        _audit.EmitAsync(
            new VaultAuditEvent
            {
                Action = action,
                WorkspaceId = envelope.WorkspaceId,
                ActorUserId = actorUserId,
                EnvelopeId = envelope.EnvelopeId,
                CredentialId = envelope.CredentialId,
                Version = envelope.Version,
                DeviceId = deviceId,
                OccurredAt = _clock.GetUtcNow(),
            },
            ct);
}
