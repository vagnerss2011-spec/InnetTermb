using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace RemoteOps.UnitTests.NDesk.Fakes;

internal sealed class FakeHubCallerContext : HubCallerContext
{
    public FakeHubCallerContext(ClaimsPrincipal? user = null)
    {
        User = user ?? new ClaimsPrincipal(new ClaimsIdentity());
    }

    /// <summary>
    /// Operador autenticado como um JWT REAL o entrega: o middleware mapeia o claim "sub" para
    /// <see cref="ClaimTypes.NameIdentifier"/> (MapInboundClaims=true), então o id fica só em
    /// NameIdentifier — não em "sub". Regressão do bug em que o Hub lia apenas "sub".
    /// </summary>
    public static FakeHubCallerContext AuthenticatedOperator(Guid userId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            authenticationType: "jwt");
        return new FakeHubCallerContext(new ClaimsPrincipal(identity));
    }

    public override string ConnectionId { get; } = Guid.NewGuid().ToString();
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User { get; }
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort()
    {
    }
}
