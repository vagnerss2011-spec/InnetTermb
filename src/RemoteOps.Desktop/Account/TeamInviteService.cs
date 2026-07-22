using System.Net;
using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// Convite que não deu certo, com o recado que vai NA TELA (pt-BR, acionável). Existe como tipo
/// próprio porque as causas chegam aqui como coisas completamente diferentes — 400 do servidor,
/// <c>FormatException</c> do codec, <c>CryptographicException</c> do GCM — e todas precisam virar
/// uma frase que o colega entenda. Deixar qualquer uma subir crua produz "Não foi possível concluir
/// a operação", e ninguém descobre que só faltou reler o código.
/// </summary>
public sealed class TeamInviteException : Exception
{
    public TeamInviteException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// O que a tela de Equipe precisa para funcionar: o serviço de convite, o transporte do time e o
/// workspace ATIVO. Andam juntos porque convite, lista de membros e remoção são sempre PARA um
/// workspace — deixar a tela adivinhar qual seria repetir, por outro caminho, o erro que a escolha
/// do cofre ao abrir veio impedir.
/// </summary>
/// <param name="Api">
/// O transporte cru, para listar e remover membros (1e). Separado do <paramref name="Service"/> de
/// propósito: aquele existe porque o convite tem CRIPTO no meio; membros são só HTTP, e passar por
/// um "serviço" que não faz nada seria uma camada a manter sem nada a ganhar.
/// </param>
public sealed record TeamContext(TeamInviteService Service, ITeamApi Api, string WorkspaceId);

/// <summary>Convite recém-gerado — o que a tela do dono precisa mostrar.</summary>
/// <param name="Code">
/// O código de 160 bits. <b>Não vai no e-mail</b> (o servidor nunca o teve): é o dono que repassa,
/// por WhatsApp/telefone/pessoalmente. É string porque a tela o exibe e o copia, como a chave de
/// recuperação — vive um diálogo e some com o GC.
/// </param>
/// <param name="EmailDelivered">
/// <c>false</c> quando o SMTP falhou. O convite VALE do mesmo jeito; a tela avisa e o dono repassa
/// o identificador na mão. Silenciar isto deixaria o dono esperando um e-mail que nunca chegou.
/// </param>
public sealed record GeneratedTeamInvite(
    string InviteId,
    string Email,
    string Role,
    string Code,
    DateTimeOffset ExpiresAt,
    bool EmailDelivered);

/// <summary>Convite aceito: o time que a conta acabou de ganhar e o cofre local dele.</summary>
/// <param name="SessionRefreshRequired">
/// O servidor avisou que o token na mão ficou obsoleto (a conta passou a pertencer a dois tenants).
/// Sem renovar, TUDO do time volta 403 — e pareceria "cofre inacessível sem motivo".
/// </param>
public sealed record AcceptedTeamInvite(
    string WorkspaceId,
    string WorkspaceName,
    string Role,
    string VaultWorkspaceId,
    bool SessionRefreshRequired);

/// <summary>
/// Os dois lados do convite de time, com a cripto acontecendo SÓ aqui e no
/// <see cref="TeamInviteCrypto"/> — o transporte (<see cref="ITeamApi"/>) move base64 e nada mais.
///
/// <list type="bullet">
///   <item><b>Dono:</b> sorteia o código, garante que o time TEM uma WK, embrulha essa WK sob a
///   chave derivada do código e sobe blob + hash. O código fica com ele, para repassar por fora.</item>
///   <item><b>Convidado:</b> troca a prova do código pelo blob, abre a WK, IMPORTA no ring e só
///   então grava o aceite com a WK re-embrulhada sob a própria AMK.</item>
/// </list>
///
/// <para><b>A ordem do aceite não é estética.</b> Importar depois de gravar a membership abriria uma
/// janela em que o app já se considera do time sem ter a chave — e o <c>CredentialVault</c> chama
/// <c>GetOrCreateWorkspaceKeyAsync</c> em toda operação. Qualquer coisa que tocasse o cofre nessa
/// janela sortearia uma WK aleatória, e o convidado passaria semanas cadastrando senhas que ninguém
/// do time abre. O ring do convidado roda fail-closed justamente porque cinto e suspensório, aqui,
/// custam uma linha.</para>
/// </summary>
public sealed class TeamInviteService
{
    /// <summary>
    /// Versão do esquema da chave do time. Existe desde o dia 1 (achado da verificação): sem ela,
    /// uma rotação futura produz estado misto v1/v2 INDETECTÁVEL, que vira erro mudo no PC do colega.
    /// </summary>
    private const int WkVersionV1 = 1;

    private readonly ITeamApi _api;
    private readonly WkWorkspaceKeyRing _keyRing;

    public TeamInviteService(ITeamApi api, WkWorkspaceKeyRing keyRing)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(keyRing);
        _api = api;
        _keyRing = keyRing;
    }

    /// <summary>
    /// Gera o convite. O código sorteado volta para a TELA (nunca para o servidor, nunca para o
    /// e-mail): quem o entrega é o operador, pelo canal que ele escolher.
    /// </summary>
    public async Task<GeneratedTeamInvite> CreateInviteAsync(
        string workspaceId, string email, string role, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        string code = TeamInviteCrypto.GenerateCode();
        string vaultWorkspaceId = AppRuntime.TeamVaultWorkspace(workspaceId);

        // GetOrCreate, e não Create: convidar a segunda pessoa NÃO pode sortear outra chave — isso
        // deixaria a primeira com um cofre que ninguém mais abre. Este é o único caminho do app em
        // que a WK de um time pode nascer (é o ato de compartilhar que cria o time).
        using WorkspaceKey wk = await _keyRing
            .GetOrCreateWorkspaceKeyAsync(vaultWorkspaceId, ct)
            .ConfigureAwait(false);

        var request = new CreateTeamInviteRequest(
            email.Trim(),
            role.Trim(),
            TeamInviteCrypto.HashCode(code),
            Convert.ToBase64String(TeamInviteCrypto.WrapWorkspaceKey(wk.Key.Span, code)),
            WkVersionV1);

        CreateTeamInviteResponse response = await _api
            .CreateInviteAsync(workspaceId, request, ct)
            .ConfigureAwait(false);

        return new GeneratedTeamInvite(
            response.InviteId,
            response.Email,
            response.Role,
            code,
            response.ExpiresAt,
            response.EmailDelivered);
    }

    /// <summary>
    /// Aceita o convite: prova o código, abre a WK do time, IMPORTA e só então grava a membership
    /// com a chave re-embrulhada sob a AMK desta conta.
    /// </summary>
    /// <exception cref="TeamInviteException">
    /// Código errado/torto, convite vencido, já usado ou de outro e-mail — tudo com a mesma cara,
    /// porque o servidor recusa tudo igual (anti-enumeração) e o cliente não tem como distinguir.
    /// </exception>
    public async Task<AcceptedTeamInvite> AcceptInviteAsync(
        string inviteId, string code, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inviteId);

        // 1) O código vira prova ANTES de qualquer rede. Um código digitado com caractere fora do
        //    alfabeto morre aqui, com recado de gente, sem gastar uma tentativa no servidor.
        string codeHash;
        try
        {
            codeHash = TeamInviteCrypto.HashCode(code ?? string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new TeamInviteException(
                "O código do convite não está no formato esperado. Ele tem letras e números em "
                + "grupos de quatro (ex.: ABCD-EFGH-...). Confira com quem convidou e digite de novo.",
                ex);
        }

        // 2) O blob da WK. Este passo é separado do aceite porque o cliente não tem o que enviar
        //    antes de recebê-lo — é o mesmo desenho do reset de senha por e-mail.
        TeamInviteContextResponse context;
        try
        {
            context = await _api.GetInviteContextAsync(inviteId, codeHash, ct).ConfigureAwait(false);
        }
        catch (CloudSyncException ex) when (IsInviteRefusal(ex))
        {
            throw Refused(ex);
        }

        string vaultWorkspaceId = AppRuntime.TeamVaultWorkspace(context.WorkspaceId);

        // 3) Abre a WK com o código. Blob adulterado ou código que passou no formato mas não é o
        //    certo estouram aqui — o AES-GCM autentica, então nunca sai chave torta daqui.
        byte[] wk;
        try
        {
            wk = TeamInviteCrypto.UnwrapWorkspaceKey(
                Convert.FromBase64String(context.WrappedWkByInvite), code!);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new TeamInviteException(
                "Não foi possível abrir este convite com o código informado. Confira o código com "
                + "quem convidou — ele vem por outro canal (WhatsApp, telefone), nunca por e-mail.",
                ex);
        }

        AcceptTeamInviteResponse accepted;
        try
        {
            // 4) IMPORTA ANTES DE ACEITAR. Ver o comentário da classe: a ordem inversa abre a janela
            //    em que o app é do time sem ter a chave, e é ali que o cofre bifurca em silêncio.
            //    O embrulho devolvido é exatamente o WrappedWk da membership.
            byte[] wrappedForAccount = await _keyRing
                .ImportWorkspaceKeyAsync(vaultWorkspaceId, wk, ct)
                .ConfigureAwait(false);

            accepted = await _api.AcceptInviteAsync(
                inviteId,
                new AcceptTeamInviteRequest(codeHash, Convert.ToBase64String(wrappedForAccount)),
                ct).ConfigureAwait(false);
        }
        catch (CloudSyncException ex) when (IsInviteRefusal(ex))
        {
            // A chave já foi importada e FICA: ela é a chave certa do time, e a membership pode ter
            // sido gravada por outra tentativa. Apagá-la aqui traria de volta o cenário do sorteio.
            throw Refused(ex);
        }
        finally
        {
            // O ring já tem a própria cópia; este buffer não pode ficar vivo esperando o GC.
            CryptographicOperations.ZeroMemory(wk);
        }

        return new AcceptedTeamInvite(
            accepted.WorkspaceId,
            accepted.WorkspaceName,
            accepted.Role,
            vaultWorkspaceId,
            accepted.SessionRefreshRequired);
    }

    /// <summary>
    /// Restaura a chave do time num device NOVO desta mesma conta, pelo que o servidor guarda. A AMK
    /// é portável, o embrulho em disco não é — sem isto o membro logaria em casa, sincronizaria e
    /// não abriria nada, <b>sem erro nenhum</b>.
    /// </summary>
    /// <returns>
    /// <c>true</c> se o workspace é de TIME e a chave está no lugar; <c>false</c> se o servidor
    /// respondeu que não há chave (cofre PESSOAL, cuja chave se deriva da AMK). Os dois são
    /// respostas legítimas — a distinção é o que o app usa para escolher a raiz certa.
    /// </returns>
    public async Task<bool> TryRestoreTeamKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        TeamWorkspaceKeyResponse? key = await _api.GetWorkspaceKeyAsync(workspaceId, ct).ConfigureAwait(false);
        if (key is null)
        {
            return false;
        }

        await _keyRing.RestoreWrappedWorkspaceKeyAsync(
            AppRuntime.TeamVaultWorkspace(workspaceId),
            Convert.FromBase64String(key.WrappedWk),
            ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Este workspace é de TIME (chave aleatória, compartilhada) ou PESSOAL (chave derivada da AMK)?
    /// É a pergunta que alimenta o indicador de cofre do shell.
    ///
    /// <para><b>Disco primeiro, servidor depois.</b> Ter a WK deste workspace guardada aqui já é
    /// prova de que ele é de time, e essa resposta vale offline — que é a condição normal de campo.
    /// Só quando não há chave local a pergunta vai à rede; e aí a MESMA chamada
    /// (<see cref="TryRestoreTeamKeyAsync"/>) já <b>restaura</b> a chave neste device, que é o que
    /// faz o segundo PC do membro abrir o cofre.</para>
    ///
    /// <para><b>Não engole falha de rede.</b> Um <c>catch</c> devolvendo <c>false</c> aqui faria o
    /// indicador afirmar "cofre pessoal" com toda a confiança quando o app simplesmente não
    /// perguntou — e o operador cadastraria o cliente sem ver o aviso. A exceção sobe; quem chama
    /// transforma isso em "não confirmado", visível na tela.</para>
    /// </summary>
    public async Task<bool> IsTeamWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        using (WorkspaceKey? local = await _keyRing
            .TryGetWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(workspaceId), ct)
            .ConfigureAwait(false))
        {
            if (local is not null)
            {
                return true;
            }
        }

        return await TryRestoreTeamKeyAsync(workspaceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A recusa do convite é sempre a MESMA no servidor (anti-enumeração): inexistente, expirado,
    /// usado, de outro e-mail ou código errado saem todos como 400. O cliente não pode inventar um
    /// diagnóstico que não tem — mas pode listar as possibilidades, que é o que o colega precisa
    /// para saber o que perguntar a quem convidou.
    /// </summary>
    private static bool IsInviteRefusal(CloudSyncException ex) =>
        ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound;

    private static TeamInviteException Refused(CloudSyncException inner) => new(
        "Convite inválido, expirado ou já utilizado — ou o código não confere. Confira o "
        + "identificador do convite e o código com quem convidou; o código vem por outro canal "
        + "(WhatsApp, telefone), nunca por e-mail.",
        inner);
}
