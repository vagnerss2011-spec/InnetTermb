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

    public async Task<CreateTeamWorkspaceResponse> CreateWorkspaceAsync(
        CreateTeamWorkspaceRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/workspaces")
            {
                Content = JsonContent.Create(request, options: CloudAuthChannel.Json),
            },
            ct);

        // NENHUM status vira sucesso silencioso aqui, nem o 409. "O time foi criado" quando ele não
        // foi é a mentira que faria o operador cadastrar cliente num workspace inexistente — e a WK
        // já teria nascido em disco, órfã. Quem trata o 409 é o chamador, sorteando outro GUID.
        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<CreateTeamWorkspaceResponse>(resp, ct);
    }

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

    public async Task<TeamKeyPublication> PublishWorkspaceKeyAsync(
        string workspaceId, PublishTeamWorkspaceKeyRequest request, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"/workspaces/{workspaceId}/key")
            {
                Content = JsonContent.Create(request, options: CloudAuthChannel.Json),
            },
            ct);

        // 409 é RESPOSTA do domínio, não falha de transporte: o servidor já tem um embrulho
        // DIFERENTE para esta conta. Traduzi-lo numa exceção genérica faria a tela dizer "não foi
        // possível" e engolir o único fato que importa — este computador pode estar com outra chave
        // do time. Qualquer OUTRO status continua estourando: um 403 virando "publicado" deixaria o
        // dono achando que a chave subiu quando ela nunca saiu daqui.
        if (resp.StatusCode == HttpStatusCode.Conflict)
        {
            return TeamKeyPublication.Divergent;
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        var body = await CloudAuthChannel.ReadResultAsync<PublishTeamWorkspaceKeyResponse>(resp, ct);
        return body.Stored ? TeamKeyPublication.Stored : TeamKeyPublication.AlreadyPublished;
    }

    public async Task<TeamMembersResponse> GetMembersAsync(
        string workspaceId, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/workspaces/{workspaceId}/members"), ct);

        // NENHUM status vira lista vazia aqui — nem o 403. "Este time não tem ninguém" é a mentira
        // mais cara que a tela de Equipe pode contar, e ela nasceria exatamente de um catch cordial
        // neste ponto. Quem decide o que dizer ao operador é a VM, com o erro na mão.
        if (!resp.IsSuccessStatusCode)
        {
            throw new CloudSyncException(resp.StatusCode);
        }

        return await CloudAuthChannel.ReadResultAsync<TeamMembersResponse>(resp, ct);
    }

    public async Task<TeamMemberRemoval> RemoveMemberAsync(
        string workspaceId, string userId, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"/workspaces/{workspaceId}/members/{userId}"), ct);

        // 404 e 409 são RESPOSTA do domínio, não falha de transporte — o servidor os usa para dizer
        // "essa pessoa não é membro" e "esse é o último dono". Traduzi-los em exceção genérica faria
        // a tela dizer "não foi possível remover" e engolir a única informação que resolve o caso.
        // Qualquer OUTRO status continua estourando: um 403 silencioso viraria "removi" na tela.
        return resp.StatusCode switch
        {
            HttpStatusCode.NotFound => TeamMemberRemoval.NotAMember,
            HttpStatusCode.Conflict => TeamMemberRemoval.LastOwner,
            _ when resp.IsSuccessStatusCode => TeamMemberRemoval.Removed,
            _ => throw new CloudSyncException(resp.StatusCode),
        };
    }
}
