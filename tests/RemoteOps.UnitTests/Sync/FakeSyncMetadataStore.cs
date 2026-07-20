using System.Linq;
using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

namespace RemoteOps.UnitTests.Sync;

internal sealed class FakeSyncMetadataStore : ISyncMetadataStore
{
    private readonly Dictionary<string, long> _secretsCursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pushedSecrets = new(StringComparer.Ordinal);
    private long _server;
    private long _outbox;

    public List<ConflictDetail> Conflicts { get; } = [];

    public long ServerCursor => _server;

    public long OutboxCursor => _outbox;

    /// <summary>Atalho de leitura pros testes de um workspace só.</summary>
    public long SecretsCursor => _secretsCursors.Values.Count == 0 ? 0 : _secretsCursors.Values.Max();

    public Task<SyncCursors> GetCursorsAsync(string workspaceId, CancellationToken ct = default)
        => Task.FromResult(new SyncCursors(_server, _outbox));

    public Task SaveServerCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        _server = cursor;
        return Task.CompletedTask;
    }

    public Task SaveOutboxCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        _outbox = cursor;
        return Task.CompletedTask;
    }

    public Task RecordConflictsAsync(IReadOnlyList<ConflictDetail> conflicts, CancellationToken ct = default)
    {
        Conflicts.AddRange(conflicts);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredConflict>> GetConflictsAsync(int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredConflict>>(Conflicts
            .Select(c => new StoredConflict(c.EntityType, c.EntityId, DateTimeOffset.UtcNow, c.BaseVersion, c.CurrentVersion, c.Reason))
            .Take(limit)
            .ToList());

    public Task ClearConflictsAsync(CancellationToken ct = default)
    {
        Conflicts.Clear();
        return Task.CompletedTask;
    }

    public Task<int> GetConflictCountAsync(CancellationToken ct = default) => Task.FromResult(Conflicts.Count);

    // ── Segredos (canal próprio, fora do changelog) ──────────────────────────────────────

    public Task<long> GetSecretsCursorAsync(string workspaceId, CancellationToken ct = default)
        => Task.FromResult(_secretsCursors.TryGetValue(workspaceId, out long cursor) ? cursor : 0);

    public Task SaveSecretsCursorAsync(string workspaceId, long cursor, CancellationToken ct = default)
    {
        // MAX: mesma disciplina monotônica do store real.
        _secretsCursors[workspaceId] = _secretsCursors.TryGetValue(workspaceId, out long current)
            ? Math.Max(current, cursor)
            : cursor;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, int>> GetPushedSecretsAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string key, int version) in _pushedSecrets)
        {
            if (key.StartsWith(workspaceId + "/", StringComparison.Ordinal))
            {
                result[key[(workspaceId.Length + 1)..]] = version;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(result);
    }

    public Task MarkSecretPushedAsync(
        string workspaceId, string envelopeId, int version, CancellationToken ct = default)
    {
        string key = workspaceId + "/" + envelopeId;
        _pushedSecrets[key] = _pushedSecrets.TryGetValue(key, out int current)
            ? Math.Max(current, version)
            : version;
        return Task.CompletedTask;
    }

    /// <summary>Só pros testes: força um re-pull do zero (o store real não expõe isso).</summary>
    public Task ResetSecretsCursorAsync(string workspaceId)
    {
        _secretsCursors.Remove(workspaceId);
        return Task.CompletedTask;
    }
}
