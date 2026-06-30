namespace RemoteOps.Terminal;

/// <summary>
/// Fornece o contexto de segurança do usuário corrente para chamadas ao vault.
/// O Desktop implementa via sessão autenticada; testes usam stub fixo.
/// </summary>
public interface ITerminalSecurityContext
{
    string ActorUserId { get; }
    string? DeviceId { get; }
}
