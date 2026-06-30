using System.Net;
using System.Net.Http;
using System.Text;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// <see cref="HttpMessageHandler"/> de teste: delega a um responder e captura uma cópia
/// imutável de cada request (método, URI, headers de auth/device, corpo) — evita acessar
/// o <see cref="HttpRequestMessage"/> depois que o <see cref="HttpClient"/> o descarta.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            request.Headers.TryGetValues("X-Device-Id", out IEnumerable<string>? dev) ? dev.Single() : null,
            body));

        return _responder(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? AuthScheme,
    string? AuthParameter,
    string? DeviceId,
    string? Body);
