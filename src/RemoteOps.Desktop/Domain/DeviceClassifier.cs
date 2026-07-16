using System.Text.RegularExpressions;
using RemoteOps.Contracts.Assets;

namespace RemoteOps.Desktop.Domain;

/// <summary>Classificação sugerida de um device. BadgeLabel/glifo só são usados no fallback (sem logo).</summary>
public sealed record DeviceClassification(string Role, string? VendorKey, string? BadgeLabel, int Confidence);

/// <summary>
/// Heurística LOCAL (sem rede) que sugere papel + vendor + selo a partir do que o operador digitou
/// (Vendor/Model/Protocolo). Ponto ÚNICO de classificação — a detecção ATIVA futura (banner
/// SSH/identidade RouterOS/sysDescr SNMP) entra aqui, alimentando o mesmo contrato. Regras
/// ordenadas: a primeira que casar vence. Nunca lança; devolve <see cref="DeviceRoles.Other"/> /
/// confiança 0 quando não reconhece.
/// </summary>
public static class DeviceClassifier
{
    // (vendor, model, protocol) já minúsculos e não-nulos → classificação ou null (não casou).
    private static readonly Func<string, string, string, DeviceClassification?>[] Rules =
    [
        (v, m, p) => p == "mikrotik" || v.Contains("mikrotik") || v.Contains("routeros")
            ? new(m.Contains("crs") || m.Contains("css") ? DeviceRoles.Switch : DeviceRoles.Router, "mikrotik", "ROS", 90) : null,
        (v, m, p) => v.Contains("huawei") && Regex.IsMatch(m, "^(ne|atn|ar|cx)|netengine")
            ? new(DeviceRoles.Router, "huawei", "VRP8", 90) : null,
        (v, m, p) => v.Contains("huawei") && Regex.IsMatch(m, "^(s[0-9]|ce|cloudengine)")
            ? new(DeviceRoles.Switch, "huawei", "VRP5", 85) : null,
        (v, m, p) => v.Contains("huawei") && Regex.IsMatch(m, "ma5|ea5|olt")
            ? new(DeviceRoles.Olt, "huawei", "OLT", 85) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "debian") ? new(DeviceRoles.ServerLinux, "debian", "DEB", 80) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "ubuntu") ? new(DeviceRoles.ServerLinux, "ubuntu", "UBU", 80) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "centos|rhel|red ?hat|rocky|almalinux|(^| )linux")
            ? new(DeviceRoles.ServerLinux, "linux", "LNX", 70) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "windows|win ?server") ? new(DeviceRoles.ServerWindows, "windows", "WIN", 75) : null,
        // A10 (ADC/load balancer): detecta o VENDOR pro ícone, mas sem sugerir papel — a operação
        // não usa o papel "load balancer"; o operador escolhe o Tipo se precisar.
        (v, m, p) => v.Contains("a10") ? new(DeviceRoles.Other, "a10", "A10", 40) : null,
        (v, m, p) => Regex.IsMatch(v, "cisco|ios|nx-os")
            ? new(Regex.IsMatch(m, "catalyst|nexus") ? DeviceRoles.Switch : DeviceRoles.Router, "cisco", "CSC", 70) : null,
        (v, m, p) => Regex.IsMatch(v, "juniper|junos") ? new(DeviceRoles.Router, "juniper", "JNP", 70) : null,
        (v, m, p) => p == "rdp" ? new(DeviceRoles.ServerWindows, "windows", "WIN", 50) : null,
    ];

    public static DeviceClassification Suggest(string? vendor, string? model, string? protocol)
    {
        string v = (vendor ?? string.Empty).ToLowerInvariant();
        string m = (model ?? string.Empty).ToLowerInvariant();
        string p = (protocol ?? string.Empty).ToLowerInvariant();

        foreach (var rule in Rules)
        {
            if (rule(v, m, p) is { } c)
            {
                return c;
            }
        }

        string? slug = string.IsNullOrWhiteSpace(vendor) ? null : Regex.Replace(v, "[^a-z0-9]+", "-").Trim('-');
        return new DeviceClassification(DeviceRoles.Other, slug, null, 0);
    }
}
