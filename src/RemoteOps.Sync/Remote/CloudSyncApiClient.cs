using System.Net;
using System.Net.Http.Json;

using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="ICloudSyncApi"/> sobre <see cref="HttpClient"/> (handler injetável
/// para testes). A autenticação (Bearer + <c>X-Device-Id</c> + refresh no 401) vive no
/// <see cref="CloudAuthChannel"/>, compartilhado com o <see cref="SecretsApiClient"/> — ver lá por
/// que o canal é um só. Nunca loga token, header de autorização ou patch.
/// </summary>
public sealed class CloudSyncApiClient : ICloudSyncApi
{
    private readonly CloudAuthChannel _channel;

    public CloudSyncApiClient(HttpClient http, Guid deviceId, ITokenStore tokenStore)
        : this(new CloudAuthChannel(http, deviceId, tokenStore))
    {
    }

    /// <summary>Reusa um canal já existente — é assim que os dois clientes dividem UM cache de tokens.</summary>
    internal CloudSyncApiClient(CloudAuthChannel channel) => _channel = channel;

    public async Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/sync/push")
            {
                Content = JsonContent.Create(request, options: CloudAuthChannel.Json),
            },
            ct);
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<PushResult>(resp, ct);
    }

    public async Task<PullResponse> PullAsync(
        string workspaceId, long cursor, int pageSize, CancellationToken ct = default)
    {
        string uri =
            $"/sync/pull?workspaceId={Uri.EscapeDataString(workspaceId)}&cursor={cursor}&pageSize={pageSize}";
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<PullResponse>(resp, ct);
    }

    // O LoginAsync(email, senha) saiu na Fase 1: quem autentica agora é o
    // E2eeAccountAuthenticator (authHash via IAccountApi), e os tokens chegam aqui já prontos pelo
    // ITokenStore que o AccountSyncCoordinator preencheu. Este cliente só consome a sessão — ele
    // nunca vê senha nem a cria.
}
