using RemoteOps.Contracts.Sessions;

namespace RemoteOps.Rdp;

public interface IRdpSessionProvider : IRemoteSessionProvider
{
    /// <summary>Config resolvida (host/porta/usuário/políticas) para a sessão aberta por OpenAsync.</summary>
    RdpConnectionConfig GetConnectionConfig(string sessionId);
}
