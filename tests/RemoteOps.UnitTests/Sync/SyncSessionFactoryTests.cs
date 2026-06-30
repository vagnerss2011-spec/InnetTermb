using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncSessionFactoryTests
{
    [Fact]
    public async Task Create_Builds_An_Offline_Session_Without_Touching_The_Network()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-factory");
        var options = new SyncSessionOptions
        {
            Workspace = ctx.Workspace,
            WorkspaceId = "00000000-0000-0000-0000-000000000001",
            CloudBaseUrl = new Uri("https://cloud.local"),
            DeviceId = Guid.NewGuid(),
            Vault = ctx.Vault,
            TokenRefPath = ctx.DbPath + ".tokenref",
        };

        await using SyncSession session = SyncSessionFactory.Create(options);

        Assert.Equal(SyncState.Offline, session.Orchestrator.Status.State);
    }

    [Fact]
    public async Task Create_Rejects_Non_Https_Url()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-http");
        var options = new SyncSessionOptions
        {
            Workspace = ctx.Workspace,
            WorkspaceId = "00000000-0000-0000-0000-000000000001",
            CloudBaseUrl = new Uri("http://insecure.local"),
            DeviceId = Guid.NewGuid(),
            Vault = ctx.Vault,
            TokenRefPath = ctx.DbPath + ".tokenref",
        };

        Assert.Throws<ArgumentException>(() => SyncSessionFactory.Create(options));
    }
}
