namespace RemoteOps.Rdp;

/// <summary>Contexto de segurança do usuário corrente para auditoria RDP.</summary>
public interface IRdpSecurityContext
{
    string ActorUserId { get; }
    string? DeviceId { get; }
}
