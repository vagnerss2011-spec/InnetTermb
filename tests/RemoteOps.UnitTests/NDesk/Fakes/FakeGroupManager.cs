using Microsoft.AspNetCore.SignalR;

namespace RemoteOps.UnitTests.NDesk.Fakes;

/// <summary>Registra as adições de grupo para os testes de <c>JoinSession</c>.</summary>
internal sealed class FakeGroupManager : IGroupManager
{
    public List<(string ConnectionId, string Group)> Added { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
