using System.Net;
using System.Net.Http;
using System.Text;

namespace RemoteOps.UnitTests.Desktop.Update.Fakes;

/// <summary>HttpMessageHandler de teste que devolve uma resposta fixa, sem I/O de rede real.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string? _body;

    public FakeHttpMessageHandler(HttpStatusCode status, string? body)
    {
        _status = status;
        _body = body;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status);
        if (_body is not null)
        {
            response.Content = new StringContent(_body, Encoding.UTF8, "application/json");
        }

        return Task.FromResult(response);
    }
}
