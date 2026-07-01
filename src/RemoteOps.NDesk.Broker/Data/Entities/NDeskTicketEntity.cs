namespace RemoteOps.NDesk.Broker.Data.Entities;

public sealed class NDeskTicketEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Hash SHA-256 do link token; o valor cru nunca é persistido.</summary>
    public required string LinkTokenHash { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>waiting | connected | expired | closed | denied.</summary>
    public required string Status { get; set; }

    /// <summary>Lista separada por vírgula: view,control,fileTransfer,adminElevation.</summary>
    public string PermissionsRequested { get; set; } = string.Empty;

    /// <summary>basic | control | file | administrator.</summary>
    public string? RequestedMode { get; set; }

    public string? AgentMinimumWindows { get; set; }
    public bool AgentAllowWindows7Legacy { get; set; }
    public bool AgentRequiresInstall { get; set; }

    /// <summary>Preenchido no redeem (single-use); identifica a sessão de signaling.</summary>
    public Guid? SessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
