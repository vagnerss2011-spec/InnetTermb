using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
            throw await FailAsync(resp, ct);
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
            throw await FailAsync(resp, ct);
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
            throw await FailAsync(resp, ct);
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
            throw await FailAsync(resp, ct);
        }

        return await CloudAuthChannel.ReadResultAsync<AcceptTeamInviteResponse>(resp, ct);
    }

    public async Task<TeamWorkspaceKeyResponse?> GetWorkspaceKeyAsync(
        string workspaceId, CancellationToken ct = default)
    {
        using HttpResponseMessage resp = await _channel.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"/workspaces/{workspaceId}/key"), ct);

        // ⚠️ 404 é RESPOSTA, não falha — mas a resposta é ESTREITA: "esta CONTA não guarda embrulho
        // NESTE workspace" (TeamService.GetWorkspaceKeyAsync: `membership?.WrappedWk is null`).
        //
        // Ele NÃO significa "workspace pessoal", e o comentário que dizia isso aqui foi a origem de
        // um bloqueante: o mesmo 404 sai de um TIME cujo embrulho do dono nunca subiu, e sai
        // igualzinho de uma INFRAESTRUTURA sem a rota (proxy, URL errada, backend anterior a esta
        // versão — a janela real da ordem de deploy). Lido como "não é de time", ele fazia o boot
        // gravar o dono do banco com os ~700 equipamentos do operador usando o GUID do TIME.
        //
        // Quem AFIRMA a natureza do workspace é `workspaces.kind`, que viaja na lista do login.
        // Aqui o `null` quer dizer só isto: não há embrulho para esta conta. Só este status; qualquer
        // outro erro continua estourando, senão um 403 (participação cortada) viraria "sem chave" e o
        // app abriria a raiz errada em silêncio.
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw await FailAsync(resp, ct);
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
            throw await FailAsync(resp, ct);
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
            throw await FailAsync(resp, ct);
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
            _ => throw await FailAsync(resp, ct),
        };
    }

    /// <summary>
    /// Monta a falha carregando o <c>reason</c> do ProblemDetails quando o servidor mandou um.
    ///
    /// <para><b>Por que o motivo, e não só o status:</b> a recusa "este workspace é o seu cofre
    /// pessoal" chega como 422, e sem ela o app escrevia "(servidor fora de alcance)" no painel de
    /// Logs — um recado errado sobre a única coisa que o operador poderia consertar. Casar substring
    /// do <c>detail</c> em pt-BR não serve: esse texto muda na primeira revisão.</para>
    ///
    /// <para><b>Ler o corpo não pode virar um segundo modo de falha.</b> Resposta sem corpo, com
    /// HTML de proxy ou com JSON de outro formato devolve <c>null</c> e a exceção sai com o status —
    /// exatamente o comportamento de antes. E só o <c>reason</c> é lido: nada do corpo entra na
    /// mensagem da exceção (ADR-013).</para>
    /// </summary>
    private static async Task<CloudSyncException> FailAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        string? reason = null;
        try
        {
            var problem = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (problem.ValueKind is JsonValueKind.Object
                && problem.TryGetProperty("reason", out JsonElement value)
                && value.ValueKind is JsonValueKind.String)
            {
                reason = value.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // Sem motivo legível — e isso NÃO vira "não é aquele motivo": quem lê o `Reason` trata
            // null como "o servidor não disse".
        }

        return new CloudSyncException(resp.StatusCode, reason);
    }
}
