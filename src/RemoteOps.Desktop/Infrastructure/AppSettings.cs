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

    /// <summary>Última versão de changelog que o operador já viu (badge "novidades"); null = nunca viu.</summary>
    public string? LastSeenChangelogVersion { get; init; }

    /// <summary>
    /// Liga a sincronização na nuvem (opt-in, ADR-002). Substitui a env var
    /// <c>REMOTEOPS_CLOUD_SYNC_ENABLED</c> — que continua valendo como fallback (ver <see cref="CloudConfig"/>).
    /// Só passa a valer no PRÓXIMO início do app (a conta é ativada no startup, antes do cofre/banco).
    /// </summary>
    public bool CloudSyncEnabled { get; init; }

    /// <summary>
    /// Endereço HTTPS do servidor de sync (ex.: <c>https://sync.suaempresa.com</c>). Substitui a env
    /// var <c>REMOTEOPS_CLOUD_URL</c> (fallback). HTTP é ignorado (fail-closed, ADR-013).
    /// </summary>
    public string? CloudServerUrl { get; init; }
}
