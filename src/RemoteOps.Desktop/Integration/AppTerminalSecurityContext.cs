using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class AppTerminalSecurityContext : ITerminalSecurityContext
{
    public string ActorUserId { get; } = "local-user";
    public string? DeviceId { get; } = Environment.MachineName;
}
