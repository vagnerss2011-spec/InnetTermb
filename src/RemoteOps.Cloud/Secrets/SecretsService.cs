using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Audit;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;
using RemoteOps.Cloud.Rbac;
using RemoteOps.Cloud.Sync;

namespace RemoteOps.Cloud.Secrets;

/// <summary>
/// Transporte de SecretEnvelope (spec §5). O changelog continua SEM segredo: os
/// metadados referenciam o envelope por id e o blob viaja por aqui.
///
/// O servidor trata o envelope como bytes opacos. Nenhum caminho deste serviço
/// tenta abrir, validar ou inspecionar o conteúdo — ele não tem as chaves.
///
/// RBAC: reusa sync.push/sync.pull de propósito. O envelope é parte do transporte
/// de sync, e os papéis já refletem a semântica certa — Operator tem sync.pull +
/// credential.use (precisa baixar o cofre para abrir sessão) e não tem sync.push.
/// </summary>
public sealed class SecretsService(
    AppDbContext db,
    PermissionEvaluator rbac,
    AuditService audit,
    ILogger<SecretsService> logger)
{
    public async Task<SecretUpsertResult> UpsertAsync(
        Guid workspaceId,
        SecretEnvelopeDto dto,
        PermissionContext permCtx,
        CancellationToken ct)
    {
        var check = await rbac.EvaluateAsync(permCtx, Permissions.SyncPush, ct);
        if (!check.Granted)
        {
            logger.LogWarning("Secret upsert denied for user {UserId} workspace {WorkspaceId}: {Reason}",
                permCtx.UserId, workspaceId, check.Reason);
            throw new RbacDeniedException(check.Reason);
        }

        if (!Guid.TryParse(dto.Id, out var id))
            throw new ArgumentException("id do envelope deve ser um GUID.");
        if (dto.Version < 1)
            throw new ArgumentException("version deve ser >= 1.");

        var ciphertext = Decode(dto.Ciphertext, nameof(dto.Ciphertext));
        var nonce = Decode(dto.Nonce, nameof(dto.Nonce));
        var tag = Decode(dto.Tag, nameof(dto.Tag));
        var wrappedCek = Decode(dto.WrappedCek, nameof(dto.WrappedCek));
        var cekNonce = Decode(dto.CekNonce, nameof(dto.CekNonce));
        var cekTag = Decode(dto.CekTag, nameof(dto.CekTag));
        if (string.IsNullOrWhiteSpace(dto.KeyVersion))
            throw new ArgumentException("keyVersion é obrigatório.");

        var existing = await db.SecretEnvelopes.FirstOrDefaultAsync(e => e.Id == id, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is not null)
        {
            // Um envelope pertence a UM workspace para sempre. Se o id já existe em
            // outro, recusa: com membership nos dois, aceitar deixaria um usuário
            // sobrescrever o cofre do workspace alheio só reusando o id.
            if (existing.WorkspaceId != workspaceId)
            {
                logger.LogWarning(
                    "Secret upsert rejected: envelope {EnvelopeId} belongs to workspace {OwnerWorkspaceId}, not {WorkspaceId}",
                    id, existing.WorkspaceId, workspaceId);
                return new SecretUpsertResult("conflict", 0, null, "envelope.workspace-mismatch");
            }

            // Monotonicidade: device atrasado não sobrescreve rotação mais nova.
            if (dto.Version <= existing.Version)
            {
                logger.LogInformation(
                    "Secret upsert conflict: envelope {EnvelopeId} incoming v{Incoming} <= current v{Current}",
                    id, dto.Version, existing.Version);
                return new SecretUpsertResult("conflict", existing.Cursor, existing.Version, "version.conflict");
            }
        }

        var cursor = await NextCursorAsync(workspaceId, ct);

        if (existing is null)
        {
            existing = new SecretEnvelopeEntity
            {
                Id = id,
                WorkspaceId = workspaceId,
                Ciphertext = ciphertext,
                Nonce = nonce,
                Tag = tag,
                Algorithm = dto.Algorithm ?? "AES-256-GCM",
                KeyVersion = dto.KeyVersion,
                CreatedBy = permCtx.UserId,
                CreatedAt = now,
            };
            db.SecretEnvelopes.Add(existing);
        }
        else
        {
            existing.Ciphertext = ciphertext;
            existing.Nonce = nonce;
            existing.Tag = tag;
            existing.Algorithm = dto.Algorithm ?? existing.Algorithm;
            existing.KeyVersion = dto.KeyVersion;
            existing.UpdatedAt = now;
            existing.RotatedAt = now;
        }

        existing.WrappedCek = wrappedCek;
        existing.CekNonce = cekNonce;
        existing.CekTag = cekTag;
        existing.Version = dto.Version;
        existing.Cursor = cursor;

        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditRecord(
            WorkspaceId: workspaceId,
            ActorUserId: permCtx.UserId,
            Action: "secret.upsert",
            TargetType: "SecretEnvelope",
            TargetId: id,
            DeviceId: permCtx.DeviceId,
            // Só metadados: nada aqui permite inferir o conteúdo do envelope.
            Metadata: new Dictionary<string, object?>
            {
                ["version"] = dto.Version,
                ["cursor"] = cursor,
                ["bytes"] = ciphertext.Length,
            }), ct);

        logger.LogInformation(
            "Secret upsert ok: workspace {WorkspaceId} envelope {EnvelopeId} v{Version} cursor {Cursor}",
            workspaceId, id, dto.Version, cursor);

        return new SecretUpsertResult("ok", cursor, dto.Version);
    }

    public async Task<SecretsPullResponse> PullAsync(
        Guid workspaceId,
        long since,
        int pageSize,
        PermissionContext permCtx,
        CancellationToken ct)
    {
        var check = await rbac.EvaluateAsync(permCtx, Permissions.SyncPull, ct);
        if (!check.Granted)
        {
            logger.LogWarning("Secret pull denied for user {UserId} workspace {WorkspaceId}: {Reason}",
                permCtx.UserId, workspaceId, check.Reason);
            throw new RbacDeniedException(check.Reason);
        }

        var limit = Math.Clamp(pageSize, 1, 1000);
        var rows = await db.SecretEnvelopes
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.Cursor > since)
            .OrderBy(e => e.Cursor)
            .Take(limit + 1)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        var nextCursor = page.Count > 0 ? page[^1].Cursor : since;

        return new SecretsPullResponse(page.Select(ToDto).ToList(), nextCursor, hasMore);
    }

    /// <summary>
    /// Próximo cursor do workspace. Atribuído a cada upsert (inclusive update) para
    /// que uma rotação chegue nos devices que já sincronizaram.
    ///
    /// Corrida: dois upserts concorrentes no mesmo workspace podem ler o mesmo máximo.
    /// O índice único (WorkspaceId, Cursor) transforma isso em erro de escrita → 409 →
    /// o outbox do cliente re-tenta. Escolha deliberada: falhar alto é melhor que dois
    /// envelopes com o mesmo cursor, caso em que um pull `> since` pularia um deles
    /// em silêncio. Sequência real do Postgres fica para a Fase 2 (sync robusto).
    /// </summary>
    private async Task<long> NextCursorAsync(Guid workspaceId, CancellationToken ct)
    {
        var max = await db.SecretEnvelopes
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId)
            .MaxAsync(e => (long?)e.Cursor, ct) ?? 0;
        return max + 1;
    }

    private static SecretEnvelopeDto ToDto(SecretEnvelopeEntity e) => new(
        Id: e.Id.ToString(),
        WorkspaceId: e.WorkspaceId.ToString(),
        Ciphertext: Convert.ToBase64String(e.Ciphertext),
        Nonce: Convert.ToBase64String(e.Nonce),
        Tag: Convert.ToBase64String(e.Tag),
        WrappedCek: e.WrappedCek is not null ? Convert.ToBase64String(e.WrappedCek) : string.Empty,
        CekNonce: e.CekNonce is not null ? Convert.ToBase64String(e.CekNonce) : string.Empty,
        CekTag: e.CekTag is not null ? Convert.ToBase64String(e.CekTag) : string.Empty,
        KeyVersion: e.KeyVersion,
        Version: e.Version)
    {
        Algorithm = e.Algorithm,
    };

    private static byte[] Decode(string b64, string field)
    {
        if (string.IsNullOrWhiteSpace(b64))
            throw new ArgumentException($"{field} é obrigatório.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new ArgumentException($"{field} não é base64 válido."); }
        if (bytes.Length == 0)
            throw new ArgumentException($"{field} não pode ser vazio.");
        return bytes;
    }
}
