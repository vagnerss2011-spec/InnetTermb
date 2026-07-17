using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// A parte autenticada do transporte, num lugar só: anexa <c>Authorization: Bearer</c> +
/// <c>X-Device-Id</c> e, em 401, faz refresh do token e repete a request UMA vez. Nunca loga token,
/// header de autorização ou corpo.
///
/// <para><b>Por que isto é uma classe compartilhada e não código copiado em cada cliente:</b> o
/// backend ROTACIONA o refresh token — o <c>TokenService.RefreshAsync</c> revoga o antigo (<c>stored.RevokedAt
/// = ...</c>) e emite outro. Se o <see cref="CloudSyncApiClient"/> e o <see cref="SecretsApiClient"/>
/// tivessem cada um o seu cache de tokens, os dois receberiam 401 juntos (o access token é o mesmo),
/// os dois tentariam refresh, e o segundo usaria um refresh token JÁ REVOGADO pelo primeiro → 401 →
/// sessão derrubada sem motivo, justo quando o sync de metadados e o de segredos rodam no mesmo
/// ciclo. Um canal, um cache, um refresh.</para>
/// </summary>
internal sealed class CloudAuthChannel
{
    // JsonSerializerDefaults.Web == camelCase + case-insensitive, espelhando o padrão do ASP.NET Core.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Guid _deviceId;
    private readonly ITokenStore _tokenStore;

    // Serializa o refresh: dois 401 concorrentes não podem virar dois refresh — o segundo queimaria
    // um refresh token que o primeiro acabou de revogar.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private TokenSet? _tokens;

    internal CloudAuthChannel(HttpClient http, Guid deviceId, ITokenStore tokenStore)
    {
        _http = http;
        _deviceId = deviceId;
        _tokenStore = tokenStore;
    }

    /// <summary>
    /// Envia a request autenticada; em 401 faz refresh e repete UMA vez. A request é recriada pelo
    /// factory a cada tentativa — uma <see cref="HttpRequestMessage"/> não é reutilizável.
    /// </summary>
    internal async Task<HttpResponseMessage> SendAsync(
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

    internal static async Task<T> ReadResultAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        T? value = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
        return value ?? throw new InvalidOperationException("Resposta vazia do servidor de sync.");
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
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_tokens is null)
            {
                return false;
            }

            var refreshRequest = new RefreshRequest(_tokens.RefreshToken, _deviceId.ToString());
            using var req = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh")
            {
                Content = JsonContent.Create(refreshRequest, options: Json),
            };

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            RefreshResponse? refreshed = await resp.Content.ReadFromJsonAsync<RefreshResponse>(Json, ct);
            if (refreshed is null)
            {
                return false;
            }

            _tokens = new TokenSet(refreshed.AccessToken, refreshed.RefreshToken, refreshed.ExpiresAt);
            await _tokenStore.SaveAsync(_tokens, ct);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
