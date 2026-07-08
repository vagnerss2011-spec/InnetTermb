namespace RemoteOps.Contracts.Assets;

public sealed class Endpoint
{
    public required string Id { get; init; }

    public required string AssetId { get; init; }

    /// <summary>ssh | telnet | rdp | mikrotik | ndesk.</summary>
    public required string Protocol { get; init; }

    public string? Fqdn { get; init; }

    public string? Ipv4 { get; init; }

    public string? Ipv6 { get; init; }

    public int Port { get; init; }

    public bool PreferIpv6 { get; init; } = true;

    public string? CredentialRefId { get; init; }

    public EndpointProfile? Profile { get; init; }
}

public sealed class EndpointProfile
{
    public string? VendorProfile { get; init; }

    public string? TerminalEncoding { get; init; }

    /// <summary>Perfil de segurança SSH: "auto" (default permissivo) | "strict" (só algoritmos fortes). null = auto.</summary>
    public string? SshAlgorithmProfile { get; init; }

    /// <summary>
    /// O que a tecla Backspace envia no terminal: <see cref="TerminalBackspaceModes.Del"/> ("del",
    /// 0x7F — padrão VT/xterm moderno) ou <see cref="TerminalBackspaceModes.ControlH"/> ("ctrl-h",
    /// 0x08 — BS, exigido por equipamentos legados como certas OLTs Huawei). null = padrão (DEL).
    /// Equivale à opção "Backspace key" do PuTTY.
    /// </summary>
    public string? BackspaceMode { get; init; }
}

/// <summary>Valores válidos para <see cref="EndpointProfile.BackspaceMode"/>.</summary>
public static class TerminalBackspaceModes
{
    /// <summary>Backspace envia DEL (0x7F) — padrão VT/xterm. Ctrl+? também é DEL.</summary>
    public const string Del = "del";

    /// <summary>Backspace envia BS (0x08) — o mesmo que Ctrl+H. Para equipamentos legados.</summary>
    public const string ControlH = "ctrl-h";
}
