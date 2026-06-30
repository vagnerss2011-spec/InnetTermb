using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Rdp.Fakes;

internal sealed class FakeRdpSecurityContext : IRdpSecurityContext
{
    public string ActorUserId { get; init; } = "test-user";
    public string? DeviceId { get; init; }
}
