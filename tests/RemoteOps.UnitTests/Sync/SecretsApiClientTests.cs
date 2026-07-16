using System.Net;
using System.Net.Http;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// O cliente HTTP dos endpoints de segredos: mesma disciplina do <see cref="CloudSyncApiClient"/> —
/// Bearer + X-Device-Id, refresh + retry único no 401, e nunca vazar token em exceção.
/// </summary>
public sealed class SecretsApiClientTests
{
    private static SecretEnvelopeDto Dto(string id, int version = 1) => new(
        Id: id,
        WorkspaceId: "ws-1",
        Ciphertext: Convert.ToBase64String([1, 2, 3]),
        Nonce: Convert.ToBase64String(new byte[12]),
        Tag: Convert.ToBase64String(new byte[16]),
        WrappedCek: Convert.ToBase64String(new byte[32]),
        CekNonce: Convert.ToBase64String(new byte[12]),
        CekTag: Convert.ToBase64String(new byte[16]),
        KeyVersion: "1|password|c1",
        Version: version);

    private static SecretsApiClient Client(
        FakeHttpMessageHandler handler, Guid deviceId, FakeTokenStore tokenStore)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.local") };
        return new SecretsApiClient(http, deviceId, tokenStore);
    }

    [Fact]
    public async Task PushAsync_Posts_To_Secrets_With_Bearer_And_DeviceId()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(
            new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok","cursor":42,"currentVersion":1}"""));
        SecretsApiClient client = Client(handler, deviceId, tokenStore);

        IReadOnlyList<SecretUpsertResult> results = await client.PushAsync("ws-1", [Dto("e1")]);

        SecretUpsertResult result = Assert.Single(results);
        Assert.Equal("ok", result.Status);
        Assert.Equal(42, result.Cursor);

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/secrets", sent.Uri!.AbsolutePath);
        Assert.Equal("Bearer", sent.AuthScheme);
        Assert.Equal("access-1", sent.AuthParameter);
        Assert.Equal(deviceId.ToString(), sent.DeviceId);

        // O corpo é {workspaceId, envelope} — a forma do SecretsUpsertRequest real.
        Assert.Contains("\"workspaceId\":\"ws-1\"", sent.Body);
        Assert.Contains("\"envelope\":", sent.Body);
    }

    /// <summary>
    /// O backend NÃO tem endpoint de lote: <c>POST /secrets</c> aceita UM envelope. O cliente
    /// aceita a lista por conveniência e faz o fan-out — a prova é a contagem de requests.
    /// </summary>
    [Fact]
    public async Task PushAsync_FansOut_OnePostPerEnvelope()
    {
        var tokenStore = new FakeTokenStore(new TokenSet("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok","cursor":1}"""));
        SecretsApiClient client = Client(handler, Guid.NewGuid(), tokenStore);

        IReadOnlyList<SecretUpsertResult> results =
            await client.PushAsync("ws-1", [Dto("e1"), Dto("e2"), Dto("e3")]);

        Assert.Equal(3, results.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/secrets", r.Uri!.AbsolutePath));
    }

    /// <summary>409 é resposta de NEGÓCIO (conflito de versão), não falha de transporte: parseia.</summary>
    [Fact]
    public async Task PushAsync_Parses_Conflict_On_409()
    {
        var tokenStore = new FakeTokenStore(new TokenSet("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Conflict, """
                {"status":"conflict","cursor":7,"currentVersion":3,"reason":"version.conflict"}
                """));
        SecretsApiClient client = Client(handler, Guid.NewGuid(), tokenStore);

        SecretUpsertResult result = Assert.Single(await client.PushAsync("ws-1", [Dto("e1")]));

        Assert.Equal("conflict", result.Status);
        Assert.Equal("version.conflict", result.Reason);
        Assert.Equal(3, result.CurrentVersion);
    }

    [Fact]
    public async Task PullAsync_Sends_Query_And_Parses_Response()
    {
        var deviceId = Guid.NewGuid();
        var tokenStore = new FakeTokenStore(new TokenSet("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"envelopes":[{"id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","workspaceId":"ws-1",
                "ciphertext":"AQID","nonce":"AAAAAAAAAAAAAAAA","tag":"AAAAAAAAAAAAAAAAAAAAAA==",
                "wrappedCek":"AAAA","cekNonce":"AAAA","cekTag":"AAAA","keyVersion":"1|password|c1",
                "version":2,"algorithm":"AES-256-GCM;CEK-wrap;AMK-HKDF-v1"}],
                "nextCursor":9,"hasMore":false}
                """));
        SecretsApiClient client = Client(handler, deviceId, tokenStore);

        SecretsPullResponse response = await client.PullAsync("ws-1", since: 3, pageSize: 50);

        Assert.Equal(9, response.NextCursor);
        Assert.False(response.HasMore);
        SecretEnvelopeDto dto = Assert.Single(response.Envelopes);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", dto.Id);
        Assert.Equal(2, dto.Version);
        Assert.Equal("1|password|c1", dto.KeyVersion);

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/secrets", sent.Uri!.AbsolutePath);
        Assert.Contains("workspaceId=ws-1", sent.Uri.Query);
        Assert.Contains("since=3", sent.Uri.Query);
        Assert.Contains("pageSize=50", sent.Uri.Query);
        Assert.Equal(deviceId.ToString(), sent.DeviceId);
    }

    [Fact]
    public async Task On_401_Refreshes_Token_And_Retries_Once()
    {
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
                    """{"envelopes":[],"nextCursor":9,"hasMore":false}""");
        });
        SecretsApiClient client = Client(handler, Guid.NewGuid(), tokenStore);

        SecretsPullResponse response = await client.PullAsync("ws-1", since: 0, pageSize: 100);

        Assert.Equal(9, response.NextCursor);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("access-old", handler.Requests[0].AuthParameter);
        Assert.Equal("/auth/refresh", handler.Requests[1].Uri!.AbsolutePath);
        Assert.Equal("access-new", handler.Requests[2].AuthParameter);
        Assert.Equal("access-new", tokenStore.Current!.AccessToken);
    }

    [Fact]
    public async Task NonSuccess_Response_Throws_Without_Leaking_Token()
    {
        var tokenStore = new FakeTokenStore(
            new TokenSet("super-secret-access-token", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                """{"type":"about:blank","title":"Forbidden","status":403,"detail":"rbac"}"""));
        SecretsApiClient client = Client(handler, Guid.NewGuid(), tokenStore);

        CloudSyncException ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => client.PullAsync("ws-1", 0, 100));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain("super-secret-access-token", ex.Message);
    }

    /// <summary>
    /// RBAC do servidor (Operator não tem sync.push) chega como 403 no push: tem que estourar, não
    /// virar "ok" silencioso — senão o ledger marcaria como enviado algo que nunca subiu.
    /// </summary>
    [Fact]
    public async Task PushAsync_Throws_On_403_RbacDenied()
    {
        var tokenStore = new FakeTokenStore(new TokenSet("a", "r", DateTimeOffset.UtcNow.AddMinutes(5)));
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Forbidden,
                """{"title":"Forbidden","status":403,"detail":"sync.push negado"}"""));
        SecretsApiClient client = Client(handler, Guid.NewGuid(), tokenStore);

        CloudSyncException ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => client.PushAsync("ws-1", [Dto("e1")]));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    /// <summary>
    /// Os dois clientes compartilham UM canal de auth. Não é elegância: o backend ROTACIONA o
    /// refresh token (revoga o antigo e emite outro). Com caches separados, o 401 do segundo cliente
    /// tentaria refresh com um token já revogado → sessão morta sem motivo.
    /// </summary>
    [Fact]
    public async Task SharedAuthChannel_RefreshOnce_ServesBothClients()
    {
        var tokenStore = new FakeTokenStore(
            new TokenSet("access-old", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1)));
        int refreshCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/auth/refresh")
            {
                refreshCount++;
                // O servidor real revoga o refresh antigo: um segundo refresh com ele daria 401.
                return refreshCount == 1
                    ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                        {"accessToken":"access-new","refreshToken":"refresh-new",
                        "expiresAt":"2030-01-01T00:00:00+00:00"}
                        """)
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            string? auth = req.Headers.Authorization?.Parameter;
            if (auth == "access-old")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return req.RequestUri.AbsolutePath == "/secrets"
                ? FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"envelopes":[],"nextCursor":1,"hasMore":false}""")
                : FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"changes":[],"nextCursor":1,"hasMore":false}""");
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.local") };
        var channel = new CloudAuthChannel(http, Guid.NewGuid(), tokenStore);
        var sync = new CloudSyncApiClient(channel);
        var secrets = new SecretsApiClient(channel);

        await sync.PullAsync("ws-1", 0, 100);   // 401 → refresh → ok
        await secrets.PullAsync("ws-1", 0, 100); // já usa o token novo, sem refresh

        Assert.Equal(1, refreshCount);
        Assert.Equal("access-new", tokenStore.Current!.AccessToken);
    }
}
