using System.Text.Json;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Converte o payload JSON do hint <c>workspace.changed</c> em <see cref="WorkspaceChangedHint"/>.
/// Lógica pura (sem dependência de SignalR) para ser testável.
/// </summary>
public static class SyncHintParser
{
    public static WorkspaceChangedHint? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string workspaceId = GetString(payload, "workspaceId");
        if (workspaceId.Length == 0)
        {
            return null;
        }

        long cursor = payload.TryGetProperty("cursor", out JsonElement c) && c.TryGetInt64(out long v) ? v : 0;
        return new WorkspaceChangedHint(
            workspaceId, cursor, GetString(payload, "entityType"), GetString(payload, "entityId"));
    }

    private static string GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
}
