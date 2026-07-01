using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RemoteOps.Desktop.Update;

/// <summary>
/// Lê a versão mínima exigida de um documento JSON estático servido via HTTP (ex.:
/// raw.githubusercontent.com apontando para um arquivo no repositório — sem servidor
/// próprio, sem token, ADR-019 §4). Qualquer falha (rede, status, JSON, versão) faz
/// fail-open: retorna null em vez de lançar, para nunca travar o app por causa de uma
/// checagem de política indisponível.
/// </summary>
public sealed class HttpUpdatePolicyFeedSource : IUpdatePolicyFeedSource
{
    private readonly HttpClient _http;
    private readonly Uri _policyUrl;

    public HttpUpdatePolicyFeedSource(HttpClient http, Uri policyUrl)
    {
        _http = http;
        _policyUrl = policyUrl;
    }

    public async Task<AppVersion?> GetMinimumRequiredVersionAsync(CancellationToken ct = default)
    {
        try
        {
            UpdatePolicyDocument? doc = await _http.GetFromJsonAsync<UpdatePolicyDocument>(_policyUrl, ct);
            if (doc?.MinimumRequiredVersion is not string raw
                || !AppVersion.TryParse(raw, out AppVersion version))
            {
                return null;
            }

            return version;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private sealed class UpdatePolicyDocument
    {
        [JsonPropertyName("minimumRequiredVersion")]
        public string? MinimumRequiredVersion { get; set; }
    }
}
