using Microsoft.AspNetCore.SignalR;

namespace RemoteOps.UnitTests.NDesk.Fakes;

/// <summary>Fake de IHubCallerClients que grava os envios em grupo — suficiente para verificar
/// se o Hub repassou (ou recusou repassar) um sinal, sem precisar de um SignalR TestServer real.</summary>
internal sealed class FakeHubCallerClients : IHubCallerClients
{
    public List<(string Group, string Method, object?[] Args)> GroupSends { get; } = [];

    public IClientProxy Caller => NullClientProxy.Instance;
    public IClientProxy Others => NullClientProxy.Instance;
    public IClientProxy OthersInGroup(string groupName) => new RecordingClientProxy(this, groupName);
    public IClientProxy OthersInGroups(IReadOnlyList<string> groupNames) => NullClientProxy.Instance;
    public IClientProxy All => NullClientProxy.Instance;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;
    public IClientProxy Client(string connectionId) => NullClientProxy.Instance;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => NullClientProxy.Instance;
    public IClientProxy Group(string groupName) => new RecordingClientProxy(this, groupName);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => NullClientProxy.Instance;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => NullClientProxy.Instance;
    public IClientProxy User(string userId) => NullClientProxy.Instance;
    public IClientProxy Users(IReadOnlyList<string> userIds) => NullClientProxy.Instance;

    private sealed class RecordingClientProxy(FakeHubCallerClients parent, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            parent.GroupSends.Add((group, method, args));
            return Task.CompletedTask;
        }
    }
}

internal sealed class NullClientProxy : IClientProxy
{
    public static readonly NullClientProxy Instance = new();

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
