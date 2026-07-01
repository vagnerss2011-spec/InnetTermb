namespace RemoteOps.NDesk.Broker;

/// <summary>Valores aceitos, espelhando os enums dos contracts/ndesk-*.schema.json.</summary>
internal static class NDeskEnums
{
    public static readonly string[] TicketStatuses = ["waiting", "connected", "expired", "closed", "denied"];
    public static readonly string[] Permissions = ["view", "control", "fileTransfer", "adminElevation"];
    public static readonly string[] Modes = ["basic", "control", "file", "administrator"];
    public static readonly string[] Routes = ["direct", "turn", "relayTcp", "unknown"];
    public static readonly string[] QualityProfiles = ["auto", "lowBandwidth", "balanced", "highQuality"];

    public static List<string> ParseList(string csv) =>
        string.IsNullOrEmpty(csv) ? [] : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries)];

    public static string ToCsv(IEnumerable<string> values) => string.Join(',', values);
}
