using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="IAccountApi"/> sobre <see cref="HttpClient"/> (handler injetável para
/// testes), espelhando o padrão do <see cref="CloudSyncApiClient"/>. Todos os endpoints são anônimos
/// — não há token ainda —, então não há Bearer nem refresh aqui.
///
/// Nunca loga corpo de request/response: mesmo sendo tudo opaco (spec §4.2), authHash e escrows são
/// material sensível o bastante pra ficar fora de log (no-secret-in-log, ADR-013). A
/// <see cref="CloudSyncException"/> carrega só o status HTTP.
/// </summary>
public sealed class AccountApiClient : IAccountApi
{
    // JsonSerializerDefaults.Web == camelCase + case-insensitive, espelhando o ASP.NET Core.
    // byte[] serializa como base64 (blobs em base64, spec §5).
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AccountApiClient(HttpClient http) => _http = http;

    public async Task<RegisterAccountResponse> RegisterAsync(
        RegisterAccountRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.PostAsJsonAsync("/auth/register", request, s_json, ct);
        return await ReadOrThrowAsync<RegisterAccountResponse>(resp, ct);
    }

    public async Task<KdfResponse> GetKdfAsync(string email, CancellationToken ct = default)
    {
        // O e-mail vai na query string por ser o contrato da spec §5 (GET /auth/kdf?email=). Não é
        // segredo — mas é PII em URL (log de proxy/servidor); o backend deve logar só o status.
        using HttpResponseMessage resp = await _http.GetAsync(
            $"/auth/kdf?email={Uri.EscapeDataString(email)}", ct);
        return await ReadOrThrowAsync<KdfResponse>(resp, ct);
    }

    public async Task<E2eeLoginResponse> LoginAsync(
        E2eeLoginRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _http.PostAsJsonAsync("/auth/login", request, s_json, ct);
        return await ReadOrThrowAsync<E2eeLoginResponse>(resp, ct);
    }

    private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        T? value = await resp.Content.ReadFromJsonAsync<T>(s_json, ct);
        // Corpo vazio num 2xx é contrato quebrado do servidor — não dá pra seguir sem os escrows.
        return value ?? throw new CloudSyncException(HttpStatusCode.UnprocessableContent);
    }
}
