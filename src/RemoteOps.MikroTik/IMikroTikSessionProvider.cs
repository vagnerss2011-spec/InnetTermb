using RemoteOps.Contracts.Sessions;

namespace RemoteOps.MikroTik;

// TODO: Implementar na frente feature/mikrotik-winbox.
// MVP usa WinBoxRunner para abrir winbox.exe externo (ADR-006).
// Futuro: RouterOS API-SSL / REST.
public interface IMikroTikSessionProvider : IRemoteSessionProvider
{
}
