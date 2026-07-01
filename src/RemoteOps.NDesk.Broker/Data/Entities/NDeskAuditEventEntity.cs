namespace RemoteOps.NDesk.Broker.Data.Entities;

public sealed class NDeskAuditEventEntity
{
    public Guid Id { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? TicketId { get; set; }
    public Guid? SessionId { get; set; }

    /// <summary>Operador autenticado, quando aplicável.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Nome exibido do usuário assistido (não é PII sensível), quando o ator é anônimo.</summary>
    public string? ActorDisplayName { get; set; }

    /// <summary>Ex.: ticket.created, ticket.redeemed, consent.granted, consent.denied, session.ended.</summary>
    public required string Action { get; set; }

    /// <summary>Metadados sanitizados em JSON. NUNCA contém segredo, token ou conteúdo de tela.</summary>
    public required string MetadataJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
