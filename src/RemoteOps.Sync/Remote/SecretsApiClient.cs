using System.Net;
using System.Net.Http.Json;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// <see cref="ISecretsApi"/> sobre <see cref="HttpClient"/>, com a mesma disciplina do
/// <see cref="CloudSyncApiClient"/>: Bearer + <c>X-Device-Id</c> + refresh no 401, pelo
/// <see cref="CloudAuthChannel"/> compartilhado. HTTPS é garantido pelo
/// <see cref="SyncSessionFactory"/>, que valida o scheme do <c>BaseAddress</c>.
///
/// <para>Este cliente NUNCA decifra: os campos são base64 opaco, e nada aqui abre, valida conteúdo
/// ou toca em chave. Em falha, a exceção carrega só o status HTTP — nunca corpo nem token.</para>
/// </summary>
public sealed class SecretsApiClient : ISecretsApi
{
    private readonly CloudAuthChannel _channel;

    public SecretsApiClient(HttpClient http, Guid deviceId, ITokenStore tokenStore)
        : this(new CloudAuthChannel(http, deviceId, tokenStore))
    {
    }

    /// <summary>Reusa um canal já existente — um cache de tokens só para todo o ciclo de sync.</summary>
    internal SecretsApiClient(CloudAuthChannel channel) => _channel = channel;

    public async Task<IReadOnlyList<SecretUpsertResult>> PushAsync(
        string workspaceId, IReadOnlyList<SecretEnvelopeDto> envelopes, CancellationToken ct = default)
    {
        var results = new List<SecretUpsertResult>(envelopes.Count);
        foreach (SecretEnvelopeDto envelope in envelopes)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await UpsertAsync(workspaceId, envelope, ct));
        }

        return results;
    }

    public async Task<SecretsPullResponse> PullAsync(
        string workspaceId, long since, int pageSize, CancellationToken ct = default)
    {
        string uri =
            $"/secrets?workspaceId={Uri.EscapeDataString(workspaceId)}&since={since}&pageSize={pageSize}";
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<SecretsPullResponse>(resp, ct);
    }

    private async Task<SecretUpsertResult> UpsertAsync(
        string workspaceId, SecretEnvelopeDto envelope, CancellationToken ct)
    {
        var body = new SecretsUpsertRequest(workspaceId, envelope);
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/secrets")
            {
                Content = JsonContent.Create(body, options: CloudAuthChannel.Json),
            },
            ct);

        // 409 é resposta de NEGÓCIO (conflito de versão / envelope de outro workspace), com corpo
        // útil — o orquestrador decide o que fazer. Qualquer outro não-200 é falha de transporte.
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<SecretUpsertResult>(resp, ct);
    }
}
