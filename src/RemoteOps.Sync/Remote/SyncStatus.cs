namespace RemoteOps.Sync.Remote;

/// <summary>Estado do orquestrador de sync, refletido na UI (MainViewModel.SyncStatus).</summary>
public enum SyncState
{
    Offline,
    Syncing,
    Synced,
    Error,
}

/// <summary>Estado atual do sync + contagem de conflitos pendentes.</summary>
public sealed record SyncStatus(SyncState State, int ConflictCount = 0);
