namespace RemoteOps.Contracts.NDesk;

public sealed class NDeskTicket
{
    public required string Id { get; init; }

    public required string WorkspaceId { get; init; }

    public string? CreatedBy { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>waiting | connected | expired | closed | denied.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// Id da sessão de signaling, disponível após o resgate (status connected). O operador
    /// criador o obtém no status do ticket para entrar no hub; null enquanto waiting. Só é
    /// devolvido ao próprio criador (GetStatusAsync é escopado) e ao agente que resgatou.
    /// </summary>
    public string? SessionId { get; init; }

    public List<string> PermissionsRequested { get; init; } = [];

    /// <summary>basic | control | file | administrator.</summary>
    public string? RequestedMode { get; init; }

    public string? LinkToken { get; init; }

    public NDeskAgentCompatibility? AgentCompatibility { get; init; }
}

public sealed class NDeskAgentCompatibility
{
    public string? MinimumWindows { get; init; }

    public bool AllowWindows7Legacy { get; init; }

    public bool RequiresInstall { get; init; }
}
