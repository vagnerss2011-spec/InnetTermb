using System.Net;
using System.Net.Http;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// O <see cref="AccountApiClient"/> tem que distinguir os dois 401 do login: credencial inválida
/// (→ <see cref="CloudSyncException"/>) e 2FA pendente (→ <see cref="MfaRequiredException"/>, pelo
/// corpo estruturado <c>error: "mfa_required"</c>). E o <c>totpCode</c> tem que sair no corpo.
/// </summary>
public sealed class AccountApiClientMfaTests
{
    private static AccountApiClient Client(FakeHttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://cloud.local") });

    private static E2eeLoginRequest Login(string? totp = null)
        => new("op@innet.tec.br", new byte[32], Guid.NewGuid().ToString(), "PC-A", totp);

    [Fact]
    public async Task Login_On401_WithMfaRequiredBody_ThrowsMfaRequiredException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """
                {"type":"about:blank","title":"Não autorizado","status":401,
                "detail":"Informe o código.","correlationId":"abc","error":"mfa_required"}
                """));

        await Assert.ThrowsAsync<MfaRequiredException>(() => Client(handler).LoginAsync(Login()));
    }

    [Fact]
    public async Task Login_On401_WithoutMfaMarker_ThrowsPlainCloudSyncException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """
                {"type":"about:blank","title":"Não autorizado","status":401,
                "detail":"Credenciais inválidas.","correlationId":"abc"}
                """));

        CloudSyncException ex = await Assert.ThrowsAsync<CloudSyncException>(
            () => Client(handler).LoginAsync(Login()));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task Login_On401_WithNonJsonBody_ThrowsPlainCloudSyncException()
    {
        // Corpo não-JSON (proxy, erro genérico) não pode virar exceção errada: cai no 401 comum.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("gateway timeout"),
        });

        await Assert.ThrowsAsync<CloudSyncException>(() => Client(handler).LoginAsync(Login()));
    }

    [Fact]
    public async Task Login_SendsTotpCode_InBody_OnResend()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"accessToken":"a","refreshToken":"r","expiresAt":"2030-01-01T00:00:00+00:00",
                "wrappedAmkPwd":"AQID","amkKeyVersion":1,
                "workspaces":[{"id":"ws","name":"NOC","role":"Owner"}]}
                """));

        await Client(handler).LoginAsync(Login(totp: "123456"));

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("/auth/login", sent.Uri!.AbsolutePath);
        Assert.Contains("\"totpCode\":\"123456\"", sent.Body);
    }

    [Fact]
    public async Task Login_FirstAttempt_HasNoTotpCodeValue()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"accessToken":"a","refreshToken":"r","expiresAt":"2030-01-01T00:00:00+00:00",
                "wrappedAmkPwd":"AQID","amkKeyVersion":1,
                "workspaces":[{"id":"ws","name":"NOC","role":"Owner"}]}
                """));

        await Client(handler).LoginAsync(Login()); // totp nulo

        CapturedRequest sent = Assert.Single(handler.Requests);
        Assert.DoesNotContain("\"totpCode\":\"", sent.Body); // nulo não vira código
    }
}
