using RemoteOps.Contracts.Assets;

namespace RemoteOps.Desktop.Domain;

/// <summary>
/// Fonte única de aparência por papel/vendor consumida pela UI (lista + editor).
///
/// Os glifos de papel são GEOMETRIAS VETORIAIS (path mini-language do WPF, viewport ~16x16),
/// desenhadas em traço — nítidas em qualquer DPI e sem risco de "tofu" de um codepoint de fonte
/// ausente. O <c>DeviceIcon</c> as renderiza com Stroke = <see cref="VendorColorHex"/>. São
/// desenhos simples/originais (não logos de marca); o logo do vendor entra por cima quando o PNG
/// existe em assets/logos/.
/// </summary>
public static class DeviceCatalog
{
    public static string RoleGlyphGeometry(string? role) => role switch
    {
        // caixa + 2 "antenas"
        DeviceRoles.Router => "M3,8 H13 V12 H3 Z M5,8 V5 M11,8 V5",
        // caixa + pernas de porta
        DeviceRoles.Switch => "M2,5 H14 V10 H2 Z M4,10 V12 M6,10 V12 M8,10 V12 M10,10 V12 M12,10 V12",
        // 3 unidades empilhadas (rack)
        DeviceRoles.ServerLinux => "M3,3 H13 V6 H3 Z M3,7 H13 V10 H3 Z M3,11 H13 V14 H3 Z",
        DeviceRoles.ServerWindows => "M3,3 H13 V6 H3 Z M3,7 H13 V10 H3 Z M3,11 H13 V14 H3 Z",
        // 2 unidades + portas (rack de fibra)
        DeviceRoles.Olt => "M2,4 H14 V7 H2 Z M2,9 H14 V12 H2 Z M4,7 V9 M8,7 V9 M12,7 V9",
        // parede de tijolos
        DeviceRoles.Firewall => "M2,4 H14 V12 H2 Z M2,7 H14 M2,10 H14 M6,4 V7 M10,4 V7 M8,7 V10 M4,10 V12 M12,10 V12",
        // uma entrada dividindo em duas saídas
        DeviceRoles.LoadBalancer => "M2,8 H7 M7,8 L11,4 H14 M7,8 L11,12 H14",
        // ondas wi-fi (chevrons) + ponto
        DeviceRoles.Wireless => "M4,9 L8,6 L12,9 M2,6 L8,2 L14,6 M8,11 L8,12",
        // monitor/dispositivo genérico
        _ => "M3,4 H13 V11 H3 Z M6,11 V13 M10,11 V13 M5,13 H11",
    };

    public static string RoleLabel(string? role) => role switch
    {
        DeviceRoles.Router => "Roteador",
        DeviceRoles.Switch => "Switch",
        DeviceRoles.ServerLinux => "Servidor Linux",
        DeviceRoles.ServerWindows => "Servidor Windows",
        DeviceRoles.Olt => "OLT",
        DeviceRoles.Firewall => "Firewall",
        DeviceRoles.LoadBalancer => "Load Balancer",
        DeviceRoles.Wireless => "Wireless",
        _ => "Sem tipo",
    };

    /// <summary>Cor de identidade do vendor (hex #RRGGBB). Configurável; fallback = cinza tercário do tema.</summary>
    public static string VendorColorHex(string? vendorKey) => vendorKey switch
    {
        "huawei" => "#C0392B",
        "mikrotik" => "#2D6FB8",
        "debian" => "#97C459",
        "ubuntu" => "#E56B2E",
        "linux" => "#8FB2C9",
        "windows" => "#2D8CDB",
        "a10" => "#EF9F27",
        "cisco" => "#1BA0D7",
        "juniper" => "#3FA34D",
        _ => "#647085",
    };

    /// <summary>
    /// Nome do arquivo de logo (em assets/logos/) ou null. O arquivo pode NÃO existir — o
    /// <c>DeviceIcon</c> cai no glifo de papel nesse caso.
    /// </summary>
    public static string? LogoFileName(string? vendorKey)
        => string.IsNullOrWhiteSpace(vendorKey) ? null : $"{vendorKey}.png";
}
