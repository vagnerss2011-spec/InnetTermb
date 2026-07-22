using System.Net;
using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Vault;
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

/// <summary>
/// Desfecho de publicar o embrulho da chave do time desta conta.
///
/// <para>Existe separado do <see cref="TeamKeyPublication"/> (que é a resposta do SERVIDOR) porque
/// <see cref="NoLocalKey"/> é uma decisão que acontece <b>antes</b> de qualquer rede: sem chave de
/// time neste computador não há o que publicar, e o servidor nem é incomodado.</para>
/// </summary>
public enum TeamKeyUpload
{
    /// <summary>Não há chave de time neste computador — nada a publicar, e nenhuma ida à rede.</summary>
    NoLocalKey,

    /// <summary>O servidor não tinha embrulho para esta conta e passou a ter.</summary>
    Published,

    /// <summary>O servidor já tinha o embrulho desta conta (republicação idempotente).</summary>
    AlreadyOnServer,
}

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

        // ANTES de qualquer sorteio: sem WK neste device, pergunte ao SERVIDOR se o time já tem uma
        // (é o membro — ou o dono — convidando do segundo PC, ou depois de reinstalar). Sortear aqui
        // com a chave do time já existindo lá é a bifurcação silenciosa que esta fatia inteira
        // combate: o convite levaria uma WK nova e o convidado entraria num "time" que não abre nada
        // dos outros. Só o 404 (null) autoriza o sorteio; falha de rede SOBE — "não sei" não pode
        // virar "pode sortear" (e o POST do convite exigiria a mesma rede logo em seguida).
        using (WorkspaceKey? local = await _keyRing
            .TryGetWorkspaceKeyAsync(vaultWorkspaceId, ct)
            .ConfigureAwait(false))
        {
            if (local is null)
            {
                await TryRestoreTeamKeyAsync(workspaceId, ct).ConfigureAwait(false);
            }
        }

        // GetOrCreate, e não Create: convidar a segunda pessoa NÃO pode sortear outra chave — isso
        // deixaria a primeira com um cofre que ninguém mais abre. Este é o único caminho do app em
        // que a WK de um time pode nascer (é o ato de compartilhar que cria o time).
        using WorkspaceKey wk = await _keyRing
            .GetOrCreateWorkspaceKeyAsync(vaultWorkspaceId, ct)
            .ConfigureAwait(false);

        // ⚠️ O EMBRULHO DO DONO SOBE AQUI, ANTES DO CONVITE. A WK do time NASCE nesta linha acima, e
        // até esta correção nada gravava o embrulho de quem CRIA o time — só o aceite do convidado
        // gravava o dele. Resultado: `GET /workspaces/{id}/key` devolvia 404 para o dono, o segundo
        // computador dele não tinha o que restaurar, o chaveiro sorteava uma WK₂ e o cofre do time
        // bifurcava — com o indicador ainda dizendo "cofre pessoal" para ele.
        //
        // Antes do POST, e não depois, de propósito: se o embrulho não entrar, o convite não pode
        // existir. Um convite gerado com uma chave que o próprio dono não recupera no outro PC é o
        // pior desfecho possível — ele já teria ditado o código por telefone.
        await PublishOwnWrappedKeyAsync(workspaceId, ct).ConfigureAwait(false);

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
    /// Publica no servidor o embrulho da chave do time <b>desta conta</b> — o <c>WrappedWk</c> da
    /// própria membership. Idempotente: republicar o mesmo blob não grava nada.
    ///
    /// <para><b>Por que existe:</b> até esta correção, o único caminho que gravava o embrulho era o
    /// ACEITE do convite. Quem CRIA o time nunca subia o dele, então o servidor respondia 404 para o
    /// dono e o segundo computador dele sorteava outra chave. Chamada em dois momentos: quando a WK
    /// nasce neste device (<see cref="CreateInviteAsync"/>) e no boot, como reparo de quem já tinha
    /// o time criado antes.</para>
    ///
    /// <para><b>Por que um endpoint próprio, e não um campo opcional no convite:</b> pendurar o
    /// embrulho no convite amarraria a guarda da chave ao ato social de convidar. O dono que criou o
    /// time e não convidou mais ninguém — ou cujo convite falhou DEPOIS de a chave nascer — ficaria
    /// exposto até convidar de novo, e o reparo de boot não teria em que pegar carona (fabricar um
    /// convite para consertar a própria chave mandaria e-mail para um estranho). Fora que criar
    /// convite exige <c>user.invite</c>: um membro sem essa permissão nunca conseguiria republicar o
    /// PRÓPRIO embrulho. Um endpoint dedicado tem um dono só para este campo — e esta base já
    /// aprendeu, mais de uma vez, o preço de dois escritores para a mesma verdade.</para>
    /// </summary>
    /// <exception cref="TeamInviteException">
    /// O servidor já guarda um embrulho e a reconciliação mostrou que a chave deste computador é
    /// OUTRA. É bifurcação — e o recado é alto de propósito: o servidor não troca o blob guardado
    /// (é ele que os outros computadores desta conta vão restaurar), e um "ok" aqui deixaria este
    /// device cadastrando senhas que ninguém do time abre.
    /// </exception>
    public async Task<TeamKeyUpload> PublishOwnWrappedKeyAsync(
        string workspaceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // Sem chave de time aqui não há o que publicar — e a rede nem é tocada. É o caso de todo
        // mundo que só tem cofre pessoal: o reparo de boot não pode custar round-trip a quem nunca
        // vai ter time.
        byte[]? wrapped = await _keyRing
            .TryGetWrappedWorkspaceKeyAsync(AppRuntime.TeamVaultWorkspace(workspaceId), ct)
            .ConfigureAwait(false);
        if (wrapped is null)
        {
            return TeamKeyUpload.NoLocalKey;
        }

        TeamKeyPublication outcome = await _api.PublishWorkspaceKeyAsync(
            workspaceId,
            new PublishTeamWorkspaceKeyRequest(Convert.ToBase64String(wrapped), WkVersionV1),
            ct).ConfigureAwait(false);

        if (outcome is TeamKeyPublication.Stored)
        {
            return TeamKeyUpload.Published;
        }

        if (outcome is TeamKeyPublication.AlreadyPublished)
        {
            return TeamKeyUpload.AlreadyOnServer;
        }

        // 409: o servidor já tem um embrulho DIFERENTE para esta conta. Ele não sabe (nem pode
        // saber — não tem AMK nenhuma) se é a mesma chave com nonce novo ou outra chave; quem sabe
        // isso é este cliente. Então baixamos o embrulho guardado e o ABRIMOS: se a chave for a
        // mesma, o RestoreWrapped… é no-op e está tudo certo; se for outra, ele estoura, que é
        // exatamente o alarme que a bifurcação merece. É por isso que a checagem do servidor pode
        // ser só por presença/igualdade de bytes sem enfraquecer nada.
        try
        {
            if (!await TryRestoreTeamKeyAsync(workspaceId, ct).ConfigureAwait(false))
            {
                // O servidor disse "já tenho uma" e, um instante depois, "não tenho nenhuma". Não dá
                // para afirmar nada; tentar de novo é honesto, inventar um desfecho não é.
                throw new TeamInviteException(
                    "Não foi possível confirmar a chave deste time com o servidor. Tente de novo em "
                    + "instantes.");
            }
        }
        catch (VaultException ex)
        {
            throw new TeamInviteException(
                "Este computador tem uma chave deste time DIFERENTE da que está guardada na sua "
                + "conta. Nada foi alterado no servidor. Não cadastre senhas do time por aqui até "
                + "resolver: entre em contato com quem administra o time — as senhas guardadas por "
                + "este computador podem não abrir para os colegas.",
                ex);
        }

        return TeamKeyUpload.AlreadyOnServer;
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
    ///
    /// <para><b>E por que o reparo do embrulho NÃO mora aqui:</b> esta pergunta é respondida pelo
    /// disco quando há chave local, e essa resposta vale offline — que é a condição normal de campo.
    /// Enfiar o <see cref="PublishOwnWrappedKeyAsync"/> neste caminho faria uma falha de rede
    /// derrubar uma resposta que não depende de rede: o operador sem sinal perderia justamente o
    /// aviso de que está no workspace do time. O reparo é chamado ao lado, no boot, com o desfecho
    /// dele indo para o log visível do app em vez de sequestrar o indicador.</para>
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
