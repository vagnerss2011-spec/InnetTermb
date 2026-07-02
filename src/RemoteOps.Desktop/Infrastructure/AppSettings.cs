using System.Collections.Generic;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>Configurações persistidas do usuário (%AppData%\RemoteOps\settings.json).</summary>
public sealed record AppSettings
{
    /// <summary>Feature flags habilitadas pelo usuário, por nome (ver <see cref="FeatureFlagNames"/>).</summary>
    public Dictionary<string, bool> Flags { get; init; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Identificador do tema ativo. Único por ora.</summary>
    public string Theme { get; init; } = "slate-signal-dark";

    /// <summary>Caminho do executável do WinBox configurado pela GUI (Configurações → Ferramentas externas).</summary>
    public string? WinBoxExePath { get; init; }

    /// <summary>SHA-256 fixado do executável do WinBox (validação fail-closed no launch).</summary>
    public string? WinBoxSha256 { get; init; }
}
