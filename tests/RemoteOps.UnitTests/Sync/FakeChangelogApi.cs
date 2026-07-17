using System.Linq;
using System.Text.Json;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Changelog em memória que imita o backend REAL (<c>remoteops-cloud@a94fb1e</c>,
/// <c>Sync/SyncService.cs</c>) nas regras que o cliente enxerga. Irmão do <see cref="FakeSecretsApi"/>
/// e pela mesma razão: um fake complacente faria o sync passar aqui e quebrar em campo.
///
/// <para>O que é copiado do servidor, e por quê importa:</para>
/// <list type="bullet">
///   <item><b>EntityId passa por <c>Guid</c></b> — o servidor faz <c>Guid.TryParse(change.EntityId)</c>
///   e devolve <c>entry.EntityId.ToString()</c>, formato "D" (COM hífens). O cliente gera os ids como
///   "N" (sem hífens). <b>O id NÃO round-trippa igual</b>, e é exatamente a armadilha que o
///   <c>SecretEnvelopeWireCodec</c> já teve que resolver no canal de segredos: sem canonizar na volta,
///   o <c>endpoint.asset_id</c> (ecoado verbatim, "N") não acha o <c>assets.id</c> ("D") e o host
///   chega no device B sem endereço nenhum.</item>
///   <item><b>Version = (versão atual ?? baseVersion) + 1</b>, e no pull <c>BaseVersion = Version - 1</c>.
///   É o contrato que o applier usa pra reconstruir a versão da linha.</item>
///   <item><b>Conflito de versão</b> quando <c>baseVersion &lt; versão atual</c> — a mudança NÃO entra
///   no changelog.</item>
///   <item><b>Idempotência por ClientChangeId</b> — re-push do mesmo lote é no-op.</item>
///   <item><b>SecretEnvelope é RECUSADO</b> (<c>secret-envelope.no-auto-merge</c>, ADR-003) e nunca
///   entra no changelog.</item>
///   <item><b>O patch trafega serializado</b> (JSON no banco → <c>JsonElement</c> na volta), então o
///   applier recebe <c>JsonElement</c>, não os tipos CLR que o store empurrou.</item>
/// </list>
///
/// <para>E a garantia central: <see cref="Forbid"/> registra plaintexts que NUNCA podem aparecer no
/// changelog. Todo patch que passa por aqui é varrido — metadado é claro, segredo jamais.</para>
/// </summary>
internal sealed class FakeChangelogApi : ICloudSyncApi
{
    private readonly List<Entry> _log = [];
    private readonly List<string> _forbidden = [];
    private long _nextId;

    /// <summary>Toda mudança que o servidor RECUSOU, na ordem.</summary>
    public List<ConflictDetail> Conflicts { get; } = [];

    public int PullCalls { get; private set; }

    /// <summary>Segredo em claro que não pode existir em NENHUM patch do changelog.</summary>
    public void Forbid(string plaintext) => _forbidden.Add(plaintext);

    /// <summary>Tipos de entidade que chegaram ao changelog — o que o servidor de fato guardou.</summary>
    public IReadOnlyList<string> StoredEntityTypes => _log.Select(e => e.EntityType).ToList();

    public Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
    {
        Guid ws = RequireGuid(request.WorkspaceId, nameof(request.WorkspaceId));
        long lastInsertedId = 0;
        var conflicts = new List<ConflictDetail>();

        foreach (SyncChange change in request.Changes)
        {
            AssertOpaque(change);

            // ADR-003: o changelog nunca transporta segredo. O servidor real devolve conflict e segue.
            if (string.Equals(change.EntityType, "SecretEnvelope", StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new ConflictDetail(
                    change.ClientChangeId, change.EntityType, change.EntityId,
                    change.BaseVersion, -1, "secret-envelope.no-auto-merge"));
                continue;
            }

            if (!string.IsNullOrEmpty(change.ClientChangeId)
                && _log.Any(e => e.WorkspaceId == ws
                    && e.EntityType == change.EntityType
                    && e.ClientChangeId == change.ClientChangeId))
            {
                continue; // já aplicado — push idempotente.
            }

            Guid entityId = Guid.TryParse(change.EntityId, out Guid parsed) ? parsed : Guid.NewGuid();
            int? currentVersion = CurrentVersion(ws, change.EntityType, entityId);
            if (currentVersion.HasValue && change.BaseVersion < currentVersion.Value)
            {
                conflicts.Add(new ConflictDetail(
                    change.ClientChangeId, change.EntityType, change.EntityId,
                    change.BaseVersion, currentVersion.Value, "version.conflict"));
                continue;
            }

            lastInsertedId = ++_nextId;
            _log.Add(new Entry(
                Id: lastInsertedId,
                WorkspaceId: ws,
                EntityType: change.EntityType,
                EntityId: entityId,
                Operation: change.Operation,
                Version: (currentVersion ?? change.BaseVersion) + 1,
                // Serializa AGORA, como o servidor faz: o patch vira texto e volta como JsonElement.
                PatchJson: JsonSerializer.Serialize(change.Patch),
                ClientChangeId: change.ClientChangeId));
        }

        Conflicts.AddRange(conflicts);
        return Task.FromResult(conflicts.Count > 0
            ? new PushResult("conflict", lastInsertedId > 0 ? lastInsertedId : null, conflicts)
            : new PushResult("ok", lastInsertedId > 0 ? lastInsertedId : null));
    }

    public Task<PullResponse> PullAsync(
        string workspaceId, long cursor, int pageSize, CancellationToken ct = default)
    {
        PullCalls++;
        Guid ws = RequireGuid(workspaceId, nameof(workspaceId));
        int limit = Math.Clamp(pageSize, 1, 1000);

        List<Entry> ordered = _log
            .Where(e => e.WorkspaceId == ws && e.Id > cursor)
            .OrderBy(e => e.Id)
            .Take(limit + 1)
            .ToList();

        bool hasMore = ordered.Count > limit;
        List<Entry> page = ordered.Take(limit).ToList();
        long nextCursor = page.Count > 0 ? page[^1].Id : cursor;

        IReadOnlyList<SyncChange> changes = page.Select(ToSyncChange).ToList();
        return Task.FromResult(new PullResponse(changes, nextCursor, hasMore));
    }

    private int? CurrentVersion(Guid workspaceId, string entityType, Guid entityId) => _log
        .Where(e => e.WorkspaceId == workspaceId && e.EntityType == entityType && e.EntityId == entityId)
        .OrderByDescending(e => e.Id)
        .Select(e => (int?)e.Version)
        .FirstOrDefault();

    /// <summary>Espelha o <c>SyncService.ToSyncChange</c> — inclusive o EntityId em formato "D".</summary>
    private static SyncChange ToSyncChange(Entry entry)
    {
        Dictionary<string, object?> patch =
            JsonSerializer.Deserialize<Dictionary<string, object?>>(entry.PatchJson) ?? [];

        return new SyncChange
        {
            ClientChangeId = entry.ClientChangeId,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId.ToString(), // "D", COM hífens — o servidor guarda um Guid.
            Operation = entry.Operation,
            BaseVersion = entry.Version - 1,
            Patch = patch,
        };
    }

    /// <summary>
    /// A prova de que o changelog não carrega segredo: se um plaintext proibido aparecer em qualquer
    /// valor do patch, o E2EE está furado — o servidor veria a senha em claro.
    /// </summary>
    private void AssertOpaque(SyncChange change)
    {
        if (_forbidden.Count == 0)
        {
            return;
        }

        string serialized = JsonSerializer.Serialize(change.Patch);
        foreach (string plaintext in _forbidden)
        {
            Assert.DoesNotContain(plaintext, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(plaintext, change.EntityId, StringComparison.Ordinal);
        }
    }

    private static Guid RequireGuid(string value, string field)
    {
        Assert.True(Guid.TryParse(value, out Guid parsed),
            $"{field} precisa ser GUID no servidor real (veio '{value}')");
        return parsed;
    }

    private sealed record Entry(
        long Id, Guid WorkspaceId, string EntityType, Guid EntityId, string Operation,
        int Version, string PatchJson, string? ClientChangeId);
}
