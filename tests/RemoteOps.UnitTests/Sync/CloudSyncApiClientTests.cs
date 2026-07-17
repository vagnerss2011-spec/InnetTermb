using System.Net;
using System.Net.Http;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class CloudSyncApiClientTests
{
    private static CloudSyncApiClient Client(
        FakeHttpMessageHandler handler, Guid deviceId, FakeTokenStore tokenStore)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.local") };
        return new CloudSyncApiClient(http, deviceId, tokenStore);
    }

    [Fact]
    public async Task PushAsync_Attaches_Bearer_And_DeviceId_And_Parses_Ok_Result()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(
            new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok","newCursor":42}"""));
        CloudSyncApiClient client = Client(handler, deviceId, tokenStore);

        var request = new PushRequest("ws-1",
        [
            new SyncChange { EntityType = "asset", EntityId = "e1", Operation = "created", Patch = [] },
        ]);

        PushResult result = await client.PushAsync(request);

        Assert.Equal("ok", result.Status);
        Assert.Equal(42, result.NewCursor);

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/sync/push", sent.Uri!.AbsolutePath);
        Assert.Equal("Bearer", sent.AuthScheme);
        Assert.Equal("access-1", sent.AuthParameter);
        Assert.Equal(deviceId.ToString(), sent.DeviceId);
    }

    [Fact]
    public async Task PullAsync_Sends_Query_And_Headers_And_Parses_Response()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(
            new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"changes":[{"clientChangeId":null,"entityType":"asset","entityId":"e1",
                "operation":"updated","baseVersion":2,"patch":{"name":"r1"}}],
                "nextCursor":7,"hasMore":false}
                """));
        CloudSyncApiClient client = Client(handler, deviceId, tokenStore);

        PullResponse response = await client.PullAsync("ws-1", cursor: 3, pageSize: 50);

        Assert.False(response.HasMore);
        Assert.Equal(7, response.NextCursor);
        SyncChange change = Assert.Single(response.Changes);
        Assert.Equal("asset", change.EntityType);
        Assert.Equal("updated", change.Operation);
        Assert.Equal(2, change.BaseVersion);

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/sync/pull", sent.Uri!.AbsolutePath);
        Assert.Contains("workspaceId=ws-1", sent.Uri.Query);
        Assert.Contains("cursor=3", sent.Uri.Query);
        Assert.Contains("pageSize=50", sent.Uri.Query);
        Assert.Equal("Bearer", sent.AuthScheme);
        Assert.Equal(deviceId.ToString(), sent.DeviceId);
    }

    [Fact]
    public async Task On_401_Refreshes_Token_And_Retries_Once()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(
            new TokenSet("access-old", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1)));
        int pullCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/auth/refresh")
            {
                return FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"accessToken":"access-new","refreshToken":"refresh-new",
                    "expiresAt":"2030-01-01T00:00:00+00:00"}
                    """);
            }

            pullCount++;
            return pullCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"changes":[],"nextCursor":9,"hasMore":false}""");
        });
        CloudSyncApiClient client = Client(handler, deviceId, tokenStore);

        PullResponse response = await client.PullAsync("ws-1", cursor: 0, pageSize: 100);

        Assert.Equal(9, response.NextCursor);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("access-old", handler.Requests[0].AuthParameter);
        Assert.Equal("/auth/refresh", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Contains("refresh-old", handler.Requests[1].Body);
        Assert.Equal("access-new", handler.Requests[2].AuthParameter);
        Assert.Equal(1, tokenStore.SaveCount);
        Assert.Equal("access-new", tokenStore.Current!.AccessToken);
    }

    [Fact]
    public async Task PushAsync_Parses_Conflict_Result_On_409()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(
            new TokenSet("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Conflict, """
                {"status":"conflict","newCursor":null,"conflicts":[
                {"clientChangeId":"c1","entityType":"SecretEnvelope","entityId":"s1",
                "baseVersion":1,"currentVersion":-1,"reason":"secret-envelope.no-auto-merge"}]}
                """));
        CloudSyncApiClient client = Client(handler, deviceId, tokenStore);

        PushResult result = await client.PushAsync(new PushRequest("ws-1",
        [
            new SyncChange { EntityType = "SecretEnvelope", EntityId = "s1", Operation = "updated", Patch = [] },
        ]));

        Assert.Equal("conflict", result.Status);
        ConflictDetail conflict = Assert.Single(result.Conflicts!);
        Assert.Equal("secret-envelope.no-auto-merge", conflict.Reason);
        Assert.Equal("SecretEnvelope", conflict.EntityType);
    }

    [Fact]
    public async Task NonSuccess_Response_Throws_Without_Leaking_Token()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(
            new TokenSet("super-secret-access-token", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                """{"type":"about:blank","title":"Forbidden","status":403,"detail":"rbac"}"""));
        CloudSyncApiClient client = Client(handler, deviceId, tokenStore);

        CloudSyncException ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => client.PullAsync("ws-1", 0, 100));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain("super-secret-access-token", ex.Message);
    }

    // LoginAsync_Stores_Tokens_And_Sends_DeviceId saiu junto com o CloudSyncApiClient.LoginAsync:
    // era a cobertura do login por SENHA (pré-E2EE), o caminho que a Fase 1 elimina do cliente.
    // Quem prova o login agora é E2eeAccountAuthenticatorTests (authHash, nunca senha) e quem prova
    // que os tokens chegam ao store é AccountSyncCoordinatorTests.
}
