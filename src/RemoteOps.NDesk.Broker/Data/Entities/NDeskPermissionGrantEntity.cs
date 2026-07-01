namespace RemoteOps.NDesk.Broker.Data.Entities;

/// <summary>
/// Consentimento explícito do usuário assistido. Nenhuma sessão de signaling é liberada
/// sem um registro aqui não-revogado e não-expirado (CLAUDE.md princípio 3).
/// </summary>
public sealed class NDeskPermissionGrantEntity
{
    public Guid SessionId { get; set; }
    public Guid TicketId { get; set; }

    public required string GrantedByDisplayName { get; set; }
    public string? GrantedByWindowsUser { get; set; }
    public required string GrantedByMachineName { get; set; }

    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>basic | control | file | administrator.</summary>
    public required string Mode { get; set; }

    /// <summary>Lista separada por vírgula: view,control,fileTransfer,adminElevation.</summary>
    public string Permissions { get; set; } = string.Empty;

    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? ConsentTextVersion { get; set; }
}
