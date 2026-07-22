using System.Net;
using System.Net.Http.Json;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// <see cref="ITeamApi"/> sobre <see cref="HttpClient"/>, reusando o <see cref="CloudAuthChannel"/>
/// (Bearer + <c>X-Device-Id</c> + refresh no 401) — o mesmo transporte autenticado do sync, pelo
/// mesmo motivo do <see cref="MfaApiClient"/>: um cache de token por cliente faria dois refresh
/// concorrentes e derrubaria a sessão.
///
/// <para>Nunca loga corpo nem header. Vale o dobro aqui: o corpo carrega o embrulho da WK do time, e
/// a URL do aceite carrega o id do convite (ADR-013).</para>
/// </summary>
public sealed class TeamApiClient : ITeamApi
{
    private readonly CloudAuthChannel _channel;

    public TeamApiClient(HttpClient http, Guid deviceId, ITokenStore tokenStore)
        : this(new CloudAuthChannel(http, deviceId, tokenStore))
    {
    }

    /// <summary>Reusa um canal já existente — divide o mesmo cache de tokens do sync.</summary>
    internal TeamApiClient(CloudAuthChannel channel) => _channel = channel;

    public async Task<CreateTeamInviteResponse> CreateInviteAsync(
        string workspaceId, CreateTeamInviteRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"/workspaces/{workspaceId}/invites")
            {
                Content = JsonContent.Create(request, options: CloudAuthChannel.Json),
            },
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<CreateTeamInviteResponse>(resp, ct);
    }

    public async Task<TeamInviteContextResponse> GetInviteContextAsync(
        string inviteId, string codeHash, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"/invites/{inviteId}/context")
            {
                Content = JsonContent.Create(
                    new TeamInviteContextRequest(codeHash), options: CloudAuthChannel.Json),
            },
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<TeamInviteContextResponse>(resp, ct);
    }

    public async Task<AcceptTeamInviteResponse> AcceptInviteAsync(
        string inviteId, AcceptTeamInviteRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"/invites/{inviteId}/accept")
            {
                Content = JsonContent.Create(request, options: CloudAuthChannel.Json),
            },
            ct);

        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<AcceptTeamInviteResponse>(resp, ct);
    }

    public async Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
        string workspaceId, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/workspaces/{workspaceId}/key"), ct);

        // 404 é RESPOSTA, não falha: significa "workspace pessoal, sem chave de time guardada".
        // Só este status; qualquer outro erro continua estourando, senão um 403 (membership cortada)
        // viraria "cofre pessoal" e o app abriria a raiz errada em silêncio.
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<TeamWorkspaceKeyResponse>(resp, ct);
    }
}
