using RemoteOps.Contracts.Sync;

namespace RemoteOps.Cloud.Sync;

public sealed record PullResponse(
    IReadOnlyList<SyncChange> Changes,
    long NextCursor,
    bool HasMore);

public sealed record PushRequest(
    string WorkspaceId,
    IReadOnlyList<SyncChange> Changes);

public sealed record PushResult(
    string Status,
    long? NewCursor,
    IReadOnlyList<ConflictDetail>? Conflicts = null);

public sealed record ConflictDetail(
    string? ClientChangeId,
    string EntityType,
    string EntityId,
    int BaseVersion,
    int CurrentVersion,
    string Reason);
