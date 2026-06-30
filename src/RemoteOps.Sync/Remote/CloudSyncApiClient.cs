using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using RemoteOps.Contracts.Sync;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="ICloudSyncApi"/> sobre <see cref="HttpClient"/> (handler injetável
/// para testes). Anexa <c>Authorization: Bearer</c> + <c>X-Device-Id</c> e faz refresh + retry único
/// em 401. Nunca loga token, header de autorização ou patch.
/// </summary>
public sealed class CloudSyncApiClient : ICloudSyncApi
{
    // JsonSerializerDefaults.Web == camelCase + case-insensitive, espelhando o padrão do ASP.NET Core.
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Guid _deviceId;
    private readonly ITokenStore _tokenStore;
    private TokenSet? _tokens;

    public CloudSyncApiClient(HttpClient http, Guid deviceId, ITokenStore tokenStore)
    {
        _http = http;
        _deviceId = deviceId;
        _tokenStore = tokenStore;
    }

    public async Task<PushResult> PushAsync(PushRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/sync/push")
            {
                Content = JsonContent.Create(request, options: s_json),
            },
            ct);
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await ReadResultAsync<PushResult>(resp, ct);
    }

    public async Task<PullResponse> PullAsync(
        string workspaceId, long cursor, int pageSize, CancellationToken ct = default)
    {
        string uri =
            $"/sync/pull?workspaceId={Uri.EscapeDataString(workspaceId)}&cursor={cursor}&pageSize={pageSize}";
        using HttpResponseMessage resp = await SendWithAuthAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await ReadResultAsync<PullResponse>(resp, ct);
    }

    /// <summary>
    /// Autentica com email/senha, guarda os tokens no <see cref="ITokenStore"/> usando o
    /// <c>deviceId</c> do cliente. Endpoint anônimo — não envia Bearer.
    /// </summary>
    public async Task LoginAsync(
        string email, string password, string deviceName, CancellationToken ct = default)
    {
        var loginRequest = new LoginRequest(email, password, _deviceId.ToString(), deviceName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = JsonContent.Create(loginRequest, options: s_json),
        };

        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        LoginResponse login = await resp.Content.ReadFromJsonAsync<LoginResponse>(s_json, ct)
            ?? throw new InvalidOperationException("Resposta de login vazia.");
        _tokens = new TokenSet(login.AccessToken, login.RefreshToken, login.ExpiresAt);
        await _tokenStore.SaveAsync(_tokens, ct);
    }

    /// <summary>
    /// Envia a request autenticada; em 401 faz refresh do token e repete UMA vez. A request é
    /// recriada pelo factory a cada tentativa — uma <see cref="HttpRequestMessage"/> não é reutilizável.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        _tokens ??= await _tokenStore.LoadAsync(ct);

        HttpResponseMessage response = await SendOnceAsync(requestFactory(), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && await TryRefreshAsync(ct))
        {
            response.Dispose();
            response = await SendOnceAsync(requestFactory(), ct);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        {
            if (_tokens is not null)
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
            }

            req.Headers.Add("X-Device-Id", _deviceId.ToString());
            return await _http.SendAsync(req, ct);
        }
    }

    /// <summary>Troca o refresh token por novos tokens. Endpoint anônimo — não envia Bearer.</summary>
    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (_tokens is null)
        {
            return false;
        }

        var refreshRequest = new RefreshRequest(_tokens.RefreshToken, _deviceId.ToString());
        using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh")
        {
            Content = JsonContent.Create(refreshRequest, options: s_json),
        };

        using HttpResponseMessage resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return false;
        }

        RefreshResponse? refreshed = await resp.Content.ReadFromJsonAsync<RefreshResponse>(s_json, ct);
        if (refreshed is null)
        {
            return false;
        }

        _tokens = new TokenSet(refreshed.AccessToken, refreshed.RefreshToken, refreshed.ExpiresAt);
        await _tokenStore.SaveAsync(_tokens, ct);
        return true;
    }

    private static async Task<T> ReadResultAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        T? value = await resp.Content.ReadFromJsonAsync<T>(s_json, ct);
        return value ?? throw new InvalidOperationException("Resposta vazia do servidor de sync.");
    }
}
