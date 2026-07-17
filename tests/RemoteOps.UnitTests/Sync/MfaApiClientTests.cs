using System.Net;
using System.Net.Http;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Gestão de 2FA autenticada (spec Fase 3): enroll/confirm/disable batem nos endpoints certos com
/// Bearer + X-Device-Id (reusa o CloudAuthChannel do sync). Enroll devolve o segredo; confirm/disable
/// esperam 204 e transformam falha em CloudSyncException.
/// </summary>
public sealed class MfaApiClientTests
{
    private static MfaApiClient Client(FakeHttpMessageHandler handler, Guid deviceId, FakeTokenStore store)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://cloud.local") }, deviceId, store);

    private static FakeTokenStore Store()
        => new(new TokenSet("access-1", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(5)));

    [Fact]
    public async Task Enroll_Posts_Authenticated_And_Parses_Response()
    {
        var deviceId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"secretBase32":"GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
                "otpauthUri":"otpauth://totp/RemoteOps:op?secret=GEZ..."}
                """));

        MfaEnrollResponse resp = await Client(handler, deviceId, Store()).EnrollAsync();

        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", resp.SecretBase32);
        Assert.StartsWith("otpauth://totp/", resp.OtpauthUri);

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/auth/mfa/enroll", sent.Uri!.AbsolutePath);
        Assert.Equal("Bearer", sent.AuthScheme);
        Assert.Equal("access-1", sent.AuthParameter);
        Assert.Equal(deviceId.ToString(), sent.DeviceId);
    }

    [Fact]
    public async Task Enroll_On409_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Conflict, """{"detail":"já ativo"}"""));

        CloudSyncException ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => Client(handler, Guid.NewGuid(), Store()).EnrollAsync());
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
    }

    [Fact]
    public async Task Confirm_Posts_Code_And_Succeeds_On204()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Client(handler, Guid.NewGuid(), Store()).ConfirmAsync(new MfaConfirmRequest("123456"));

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("/auth/mfa/confirm", sent.Uri!.AbsolutePath);
        Assert.Equal("Bearer", sent.AuthScheme);
        Assert.Contains("\"code\":\"123456\"", sent.Body);
    }

    [Fact]
    public async Task Confirm_On400_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"detail":"código inválido"}"""));

        CloudSyncException ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => Client(handler, Guid.NewGuid(), Store()).ConfirmAsync(new MfaConfirmRequest("000000")));
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public async Task Disable_Posts_Code_And_Succeeds_On204()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await Client(handler, Guid.NewGuid(), Store()).DisableAsync(new MfaDisableRequest("654321"));

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("/auth/mfa/disable", sent.Uri!.AbsolutePath);
        Assert.Contains("\"code\":\"654321\"", sent.Body);
    }

    [Fact]
    public async Task Disable_On400_Throws()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, """{"detail":"código inválido"}"""));

        await Assert.ThrowsAsync<CloudSyncException>(
            () => Client(handler, Guid.NewGuid(), Store()).DisableAsync(new MfaDisableRequest("000000")));
    }
}
