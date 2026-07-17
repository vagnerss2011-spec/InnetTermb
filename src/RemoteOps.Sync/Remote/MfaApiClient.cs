using System.Net;
using System.Net.Http.Json;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="IMfaApi"/> sobre <see cref="HttpClient"/>, reusando o
/// <see cref="CloudAuthChannel"/> (Bearer + <c>X-Device-Id</c> + refresh no 401) — o mesmo transporte
/// autenticado do sync. Nunca loga token, header de autorização ou corpo (o corpo do enroll traz o
/// segredo TOTP em Base32).
/// </summary>
public sealed class MfaApiClient : IMfaApi
{
    private readonly CloudAuthChannel _channel;

    public MfaApiClient(HttpClient http, Guid deviceId, ITokenStore tokenStore)
        : this(new CloudAuthChannel(http, deviceId, tokenStore))
    {
    }

    /// <summary>Reusa um canal já existente — divide o mesmo cache de tokens do sync.</summary>
    internal MfaApiClient(CloudAuthChannel channel) => _channel = channel;

    public async Task<MfaEnrollResponse> EnrollAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/auth/mfa/enroll"), ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<MfaEnrollResponse>(resp, ct);
    }

    public Task ConfirmAsync(MfaConfirmRequest request, CancellationToken ct = default)
        => PostNoContentAsync("/auth/mfa/confirm", request, ct);

    public Task DisableAsync(MfaDisableRequest request, CancellationToken ct = default)
        => PostNoContentAsync("/auth/mfa/disable", request, ct);

    /// <summary>POST que espera 204. Código inválido/servidor fora vira <see cref="CloudSyncException"/>.</summary>
    private async Task PostNoContentAsync(string uri, object body, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(body, options: CloudAuthChannel.Json),
            },
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }
    }
}
