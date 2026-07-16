namespace RemoteOps.Contracts.Assets;

/// <summary>
/// Papéis normalizados de um device (classificação por tipo). Distinto de <c>Vendor</c>: um mesmo
/// vendor (ex.: Huawei) tem roteador (VRP8) E switch (VRP5). Ver
/// docs/superpowers/specs/2026-07-15-classificador-device-design.md.
/// </summary>
public static class DeviceRoles
{
    public const string Router = "router";
    public const string Switch = "switch";
    public const string ServerLinux = "server-linux";
    public const string ServerWindows = "server-windows";
    public const string Olt = "olt";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        [Router, Switch, ServerLinux, ServerWindows, Olt, Other];
}
