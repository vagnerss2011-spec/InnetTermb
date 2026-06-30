using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class FakeTerminalSecurityContext : ITerminalSecurityContext
{
    public string ActorUserId { get; init; } = "test-user";
    public string? DeviceId { get; init; }
}
