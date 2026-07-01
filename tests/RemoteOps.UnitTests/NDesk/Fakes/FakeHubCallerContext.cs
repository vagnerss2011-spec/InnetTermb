using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace RemoteOps.UnitTests.NDesk.Fakes;

internal sealed class FakeHubCallerContext : HubCallerContext
{
    public override string ConnectionId { get; } = Guid.NewGuid().ToString();
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User { get; } = new ClaimsPrincipal(new ClaimsIdentity());
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
}
