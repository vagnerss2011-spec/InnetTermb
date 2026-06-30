using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class AppTerminalSecurityContext : ITerminalSecurityContext, RemoteOps.Rdp.IRdpSecurityContext
{
    public string ActorUserId { get; } = "local-user";
    public string? DeviceId { get; } = Environment.MachineName;
}
