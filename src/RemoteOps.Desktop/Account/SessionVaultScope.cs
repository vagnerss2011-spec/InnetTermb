using System.IO;

using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// Que cofre esta sessão do app abriu.
///
/// <para><b>Por que é público</b> (o <see cref="SessionVaultScope"/> continua interno): esta é a
/// pergunta que o convite tem de fazer antes de deixar qualquer dado sair da máquina. O
/// <c>TeamInviteService</c> e a tela de Equipe são públicos e precisam recebê-la pelo construtor —
/// deduzi-la por outro caminho criaria uma segunda fonte da verdade sobre o cofre aberto, que é
/// exatamente a divergência que esta fatia inteira existe para matar.</para>
/// </summary>
public enum SessionVaultKind
{
    /// <summary>Cofre pessoal: <c>ws-local</c>, raiz AMK, <c>sync-local.db</c>. O app de sempre.</summary>
    Personal,

    /// <summary>Cofre do TIME, com a chave presente: senhas abrem e são gravadas sob a WK.</summary>
    Team,

    /// <summary>
    /// Cofre do TIME <b>sem a chave neste computador</b>. O app abre e os equipamentos aparecem;
    /// toda operação de senha recusa ALTO. É o desenho funcionando — e é por isso que o indicador
    /// precisa dizer isso na tela, senão vira "o SSH não conecta" no meio de um atendimento.
    /// </summary>
    TeamWithoutKey,
}

/// <summary>
/// Em qual cofre esta sessão do app escreve. Decidido UMA vez, no boot, e nunca no meio: trocar de
/// cofre com a UI viva exigiria trocar cofre, banco, store e todos os ViewModels ao mesmo tempo.
/// </summary>
/// <param name="VaultWorkspaceId">
/// A identidade do COFRE (<c>ws-local</c> ou <c>time:{W}</c>) — onde os envelopes de senha moram.
/// </param>
/// <param name="DbName">
/// ⚠️ <b>NÃO é o <paramref name="VaultWorkspaceId"/>.</b>
/// <c>LocalSyncClientFactory.OpenWorkspaceAsync</c> recusa <c>Path.GetInvalidFileNameChars()</c>, e
/// ':' é um deles no Windows — passar <c>time:{W}</c> como nome de banco derruba o <c>OnStartup</c>
/// com "Não foi possível iniciar". Duas nomenclaturas, de propósito.
/// </param>
/// <param name="VaultAlgorithm">
/// A raiz das senhas desta sessão. Vai para o sync: quando o fio NÃO ecoa <c>algorithm</c>, é este
/// valor que decide como o envelope é gravado — assumir AMK num cofre de time produziria um
/// envelope que monta o AAD errado e nunca abre.
/// </param>
internal sealed record SessionVaultScope(
    string VaultWorkspaceId, string DbName, string VaultAlgorithm, SessionVaultKind Kind)
{
    /// <summary>O escopo de hoje, byte a byte: os mesmos ids, a mesma raiz, o mesmo banco.</summary>
    internal static SessionVaultScope Personal { get; } = new(
        AppRuntime.CredentialsWorkspace,
        AppRuntime.DbWorkspace,
        VaultAlgorithms.AmkRootedV1,
        SessionVaultKind.Personal);

    internal static SessionVaultScope Team(string serverWorkspaceId, bool temChave) => new(
        AppRuntime.TeamVaultWorkspace(serverWorkspaceId),  // "time:{W}" — COFRE
        AppRuntime.TeamDbName(serverWorkspaceId),          // "team-{W}" — ARQUIVO, sem ':'
        VaultAlgorithms.WkRootedV1,
        temChave ? SessionVaultKind.Team : SessionVaultKind.TeamWithoutKey);
}

/// <summary>
/// O app não conseguiu decidir, com honestidade, em qual cofre esta sessão escreve — e por isso
/// RECUSOU abrir. A mensagem é escrita, em pt-BR, e vai para a tela: um erro genérico aqui viraria
/// "não abre e não diz por quê", que é justamente o desfecho que esta fatia existe para matar.
/// </summary>
internal sealed class SessionVaultScopeException : Exception
{
    internal SessionVaultScopeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Decide o <see cref="SessionVaultScope"/> no boot, <b>a partir do disco</b>. Rede só na
/// primeiríssima abertura de um workspace que esta máquina nunca viu — e nunca mais depois.
///
/// <para><b>Por que do disco:</b> um escopo que dependesse de rede mudaria de valor conforme o sinal
/// do operador. Mudar de cofre por causa do sinal é escrever no banco errado sem nada na tela.</para>
///
/// <para><b>Três fatos persistidos, nenhum formato novo:</b> o marcador de raiz
/// (<c>KeyRooting["time:{W}"] == WkRandom</c>), gravado pela própria raiz do time sempre que a WK
/// aterrissa; o arquivo <c>sync-local.owner</c>, que diz qual workspace de servidor é DONO do
/// banco pessoal desta máquina; e o <c>sync_cursor</c> DENTRO do próprio <c>sync-local.db</c>, que
/// diz contra qual workspace aquele banco vinha sincronizando (<see cref="PersonalDbOwnerProbe"/>).
/// O segundo é um arquivo NOVO, e não um rename de <c>sync-local.db</c>, de propósito: um rename
/// meio-feito (banco movido, <c>.keyref</c> para trás) deixaria o operador sem os ~700 e sem caminho
/// de volta; um marcador que falha ao ser escrito não perde nada. O terceiro não é gravado por
/// ninguém aqui — é só LIDO, e é ele que faz a regra 5 parar de adotar dono sem evidência.</para>
/// </summary>
internal sealed class SessionVaultScopeResolver
{
    private readonly WkWorkspaceKeyRing _teamKeyRing;
    private readonly IVaultRootingStore _rooting;
    private readonly string _ownerMarkerPath;
    private readonly string _personalDbPath;

    /// <param name="personalDbPath">
    /// O caminho do banco pessoal (<c>sync-local.db</c>). A EXISTÊNCIA dele separa "máquina que já
    /// usava o RemoteOps antes dos times" de "máquina nova" (onde a sonda decide, porque o primeiro
    /// workspace aberto aqui pode muito bem ser o do TIME).
    /// <para>⚠️ <b>Existir não é mais suficiente para adotar o dono.</b> Era, e era por isso que o
    /// operador abrindo o TIME gravava o GUID do time como dono do banco com os ~700 clientes dele.
    /// Quem responde de quem é o banco é o CONTEÚDO dele — ver o parâmetro
    /// <c>readPersonalDbWorkspacesAsync</c> do <see cref="ResolveAsync"/>.</para>
    /// </param>
    internal SessionVaultScopeResolver(
        WkWorkspaceKeyRing teamKeyRing, IVaultRootingStore rooting, string ownerMarkerPath,
        string personalDbPath)
    {
        ArgumentNullException.ThrowIfNull(teamKeyRing);
        ArgumentNullException.ThrowIfNull(rooting);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerMarkerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalDbPath);

        _teamKeyRing = teamKeyRing;
        _rooting = rooting;
        _ownerMarkerPath = ownerMarkerPath;
        _personalDbPath = personalDbPath;
    }

    /// <param name="probeIsTeamAsync">
    /// A sonda online (<c>TeamInviteService.IsTeamWorkspaceAsync</c>): disco → rede, e a MESMA ida já
    /// restaura a chave. Só é chamada quando o disco não sabe responder.
    /// </param>
    /// <param name="readPersonalDbWorkspacesAsync">
    /// ⚠️ <b>A evidência autoritativa da regra 5</b> (<see cref="PersonalDbOwnerProbe"/>): com quais
    /// workspaces de servidor o <c>sync-local.db</c> desta máquina já sincronizou. <c>null</c> (o
    /// delegate ou a resposta dele) significa <b>não deu para olhar</b>, nunca "não há".
    /// <para>É opcional porque o modo local (sem conta) não tem banco de sync a interrogar e a
    /// resolução nem chega à regra 5 — mas o boot com conta PASSA o delegate: sem ele a regra 5
    /// volta a adotar o dono só porque o arquivo existe, que é o vazamento que ela existe para
    /// fechar.</para>
    /// </param>
    /// <param name="accountWorkspaceCount">
    /// Quantos workspaces esta conta tem (<c>AccountSession.Workspaces</c>), ou <c>null</c> quando
    /// esta sessão não recebeu a lista (relaunch pelo cache da AMK, que não fala com o servidor).
    /// <para><b>2+ nunca adota offline:</b> quem tem dois workspaces tem um time, e só quem tem um
    /// time consegue estar abrindo um. É a segunda rede, para o banco pessoal que ainda não
    /// sincronizou com ninguém e portanto não tem o que dizer sobre si. Ela não tranca ninguém:
    /// a lista chega pelo LOGIN, e o chooser que oferece o time é do mesmo login.</para>
    /// </param>
    /// <exception cref="SessionVaultScopeException">
    /// Não deu para decidir com honestidade. Recusar é obrigatório: assumir "pessoal" abriria
    /// <c>sync-local.db</c> e <c>ws-local</c> sincronizando contra um workspace de servidor que não é
    /// dono deles — os hosts pessoais do operador subiriam para o workspace de outra pessoa.
    /// </exception>
    internal async Task<SessionVaultScope> ResolveAsync(
        string serverWorkspaceId,
        Func<string, CancellationToken, Task<bool>> probeIsTeamAsync,
        Func<CancellationToken, Task<IReadOnlyList<string>?>>? readPersonalDbWorkspacesAsync = null,
        int? accountWorkspaceCount = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverWorkspaceId);
        ArgumentNullException.ThrowIfNull(probeIsTeamAsync);

        (bool legivel, string? dono) = ReadOwnerMarker();

        // REGRA 1 — o caso do operador, sempre. E ela vem ANTES de qualquer regra de time: é isto
        // que impede o app dele de mudar de cofre sozinho por causa do blob `wk:time:{W_pessoal}`
        // que o build 1e gravou no instante em que ele clicou "convidar".
        if (legivel && string.Equals(dono, serverWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            return SessionVaultScope.Personal;
        }

        // REGRA 2 — marcador ILEGÍVEL não é marcador AUSENTE. O arquivo está aí, dizendo quem é o
        // dono do banco pessoal — só não deu para ler AGORA (antivírus, backup, permissão). Tratar
        // isso como "primeira ativação" regravaria o dono com o workspace da vez: se for o do TIME,
        // o banco pessoal muda de dono em silêncio e cada boot seguinte o abre como pessoal do time.
        // E as regras de evidência tampouco podem rodar: sem saber o dono, a chave de time em disco
        // não distingue "workspace do time" de "workspace pessoal com o blob que o 1e deixou".
        // "Não sei quem é o dono" recusa alto; a próxima abertura, com o arquivo legível, volta ao
        // normal — nada é perdido nem regravado.
        if (!legivel)
        {
            throw new SessionVaultScopeException(
                "O arquivo que identifica o dono do cofre local ('sync-local.owner') está ilegível "
                + "neste momento. Nada foi alterado. Feche e abra o RemoteOps de novo; se o aviso "
                + "continuar, verifique permissões/antivírus na pasta de dados do RemoteOps.");
        }

        string vaultWorkspaceId = AppRuntime.TeamVaultWorkspace(serverWorkspaceId);

        // REGRA 3 — a WK está aqui: é cofre de time, e a resposta vale OFFLINE (a condição normal
        // de campo, e o caminho de todo boot depois do primeiro). Ela vem ANTES da adoção do dono
        // (regra 5) de propósito: um workspace com chave de time em disco NUNCA pode ser adotado
        // como dono do banco pessoal — nem quando o marcador se perdeu. Adotar aqui amarraria
        // `sync-local.db` ao workspace do TIME: os hosts pessoais subiriam para os colegas e os do
        // time desceriam para o banco pessoal, sem um único erro na tela.
        if (await HasTeamKeyAsync(vaultWorkspaceId, ct).ConfigureAwait(false))
        {
            return SessionVaultScope.Team(serverWorkspaceId, temChave: true);
        }

        // REGRA 4 — a chave já aterrissou aqui um dia (marcador de raiz), mas o blob não está mais.
        // Cair em "pessoal" aqui é o desastre; fail-closed é a resposta.
        if (await _rooting.LoadKeyRootingAsync(vaultWorkspaceId, ct).ConfigureAwait(false)
            is VaultKeyRooting.WkRandom)
        {
            return SessionVaultScope.Team(serverWorkspaceId, temChave: false);
        }

        // REGRA 5 — sem dono registrado e sem NENHUMA evidência de time para este workspace.
        if (dono is null)
        {
            bool temBancoPessoal = File.Exists(_personalDbPath);

            // ⚠️ A EVIDÊNCIA, e não mais a mera existência do arquivo. O comentário que morava aqui
            // dizia que `sync-local.db` era o banco pessoal deste workspace "por construção, porque
            // não existia como criar workspace de time". Isso era verdade ANTES desta fatia e virou
            // FALSO nela: com o operador abrindo o TIME, adotar gravava `sync-local.owner` = GUID do
            // time e amarrava o banco com os ~700 clientes dele ao workspace dos colegas — offline,
            // sem sonda e sem uma linha na tela. Quem sabe de quem é o banco é o próprio banco.
            DonoDoBanco evidencia = temBancoPessoal
                ? Classificar(
                    await LerWorkspacesDoBancoAsync(readPersonalDbWorkspacesAsync, ct)
                        .ConfigureAwait(false),
                    serverWorkspaceId)
                : DonoDoBanco.NaoSei;

            // 5a — ADOÇÃO OFFLINE, o caso de TODA a frota no upgrade. Ela sobrevive porque a
            // evidência do banco do operador é positiva (ele vinha sincronizando com este mesmo
            // workspace) ou, na pior das hipóteses, ausente numa conta de um workspace só — onde
            // não existe outro dono possível.
            if (temBancoPessoal && PodeAdotarSemRede(evidencia, accountWorkspaceCount))
            {
                WriteOwnerMarker(serverWorkspaceId);
                return SessionVaultScope.Personal;
            }

            // 5b — a sonda decide. São três entradas aqui: máquina NOVA (o segundo PC do colega
            // escolhendo o time no chooser), banco pessoal cuja evidência aponta para OUTRO
            // workspace, e banco pessoal sem evidência numa conta que já tem time. Nos três, adotar
            // às cegas amarraria o banco pessoal ao workspace errado; e nos três a rede acabou de
            // ser usada para o próprio login, então perguntar não tranca ninguém que já trabalhava.
            if (await ProbeAsync(serverWorkspaceId, probeIsTeamAsync, ct).ConfigureAwait(false))
            {
                // A sonda restaura a chave na mesma ida; se não restaurou, fail-closed e alto.
                return SessionVaultScope.Team(
                    serverWorkspaceId,
                    temChave: await HasTeamKeyAsync(vaultWorkspaceId, ct).ConfigureAwait(false));
            }

            // "Não é de time". Com o banco pessoal DIZENDO que é de outro workspace, adotar aqui
            // seria a mesma mistura de acervos da regra 6 — só que decidida contra evidência, em
            // vez de na ausência dela.
            if (evidencia is DonoDoBanco.OutroWorkspace)
            {
                throw new SessionVaultScopeException(OutraInstalacaoMsg);
            }

            // É o cofre pessoal desta conta: adota como dono do banco pessoal.
            WriteOwnerMarker(serverWorkspaceId);
            return SessionVaultScope.Personal;
        }

        // REGRA 6 — há um dono registrado (outro workspace) e nenhuma evidência local: workspace
        // que esta máquina nunca viu. UM round-trip, uma vez na vida deste par (máquina,
        // workspace): a sonda restaura a chave na mesma ida, e a regra 3 responde daqui em diante.
        //
        // ⚠️ Esta recusa é certa para o caso que ela descreve — e ELA NÃO PODE ser o que o operador
        // ouve sobre o PRÓPRIO cofre. Era: a regra 5 adotava o GUID do time como dono do banco
        // pessoal e, na abertura seguinte do cofre dele, esta linha o acusava de estar abrindo cofre
        // de outra instalação, sem saída — `sync-local.owner` é invisível para ele. O conserto não
        // foi suavizar o texto (o caso real precisa dele): foi tirar da regra 5 o poder de gravar um
        // dono sem evidência. Fixado por teste (DepoisDeAbrirOTIME_OCofrePESSOAL_CONTINUA_Abrindo).
        if (!await ProbeAsync(serverWorkspaceId, probeIsTeamAsync, ct).ConfigureAwait(false))
        {
            // O servidor respondeu: não há chave de time aqui. Como este workspace TAMBÉM não é o
            // dono do banco pessoal desta máquina, ele é o cofre pessoal de outra conta/instalação.
            throw new SessionVaultScopeException(OutraInstalacaoMsg);
        }

        // A sonda restaurou a chave? Se sim, cofre do time completo. Se não, fail-closed e alto —
        // "o servidor disse que é de time" não é licença para escrever sem chave.
        return SessionVaultScope.Team(
            serverWorkspaceId,
            temChave: await HasTeamKeyAsync(vaultWorkspaceId, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A recusa de "acervo de outra instalação", numa definição só: ela sai da regra 5 (evidência
    /// contrária do banco) e da regra 6 (dono registrado diferente). Duas cópias divergiriam no
    /// primeiro ajuste de texto, e é sobre esta frase que o operador vai ligar pedindo ajuda.
    /// </summary>
    private const string OutraInstalacaoMsg =
        "Este workspace é o cofre pessoal de outra instalação do RemoteOps, e abri-lo aqui "
        + "misturaria os dois acervos. Abra o seu cofre pessoal, ou peça um convite para "
        + "entrar num time.";

    /// <summary>O que o próprio <c>sync-local.db</c> diz sobre o workspace que está sendo aberto.</summary>
    private enum DonoDoBanco
    {
        /// <summary>
        /// Nenhuma evidência CONTRÁRIA: ou não deu para olhar dentro do banco, ou ele abriu e nunca
        /// sincronizou com ninguém. Os dois viram um valor só de propósito — a decisão que sai daqui
        /// é a mesma, e um ramo que ninguém percorre é dívida disfarçada de rigor. A distinção que
        /// muda decisão é a de baixo: EVIDÊNCIA CONTRÁRIA existe, ou não existe.
        /// </summary>
        NaoSei,

        /// <summary>O banco vinha sincronizando com ESTE workspace: ele é o dono, medido.</summary>
        EsteWorkspace,

        /// <summary>O banco vinha sincronizando com OUTRO. Este workspace não é o dono dele.</summary>
        OutroWorkspace,
    }

    /// <summary>
    /// Adotar sem rede exige evidência positiva — ou ausência de evidência numa conta em que não
    /// existe outro dono possível.
    ///
    /// <para><b>Por que <see cref="DonoDoBanco.EsteWorkspace"/> adota mesmo com 2+ workspaces:</b> a
    /// contagem é heurística e a evidência é medição. Vetar aqui mandaria à rede o boot do operador
    /// que TEM um time e está abrindo o próprio cofre pessoal — e uma sonda que respondesse "não é
    /// de time" acabaria recusando o cofre dele. A rede da contagem existe para o "não sei", que é
    /// onde ela é a única coisa que sobra.</para>
    /// </summary>
    private static bool PodeAdotarSemRede(DonoDoBanco evidencia, int? accountWorkspaceCount)
        => evidencia switch
        {
            DonoDoBanco.EsteWorkspace => true,
            DonoDoBanco.OutroWorkspace => false,
            _ => accountWorkspaceCount is not >= 2,
        };

    private static DonoDoBanco Classificar(IReadOnlyList<string>? workspaces, string serverWorkspaceId)
    {
        if (workspaces is null || workspaces.Count == 0)
        {
            return DonoDoBanco.NaoSei;
        }

        return workspaces.Contains(serverWorkspaceId, StringComparer.OrdinalIgnoreCase)
            ? DonoDoBanco.EsteWorkspace
            : DonoDoBanco.OutroWorkspace;
    }

    /// <summary>
    /// Lê a evidência do banco sem NUNCA derrubar o boot por causa dela: qualquer falha vira "não
    /// sei", que é o valor que não autoriza afirmar nada. Um erro de I/O aqui não pode virar o
    /// motivo de o app não abrir — mas também não pode virar licença para adotar.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> LerWorkspacesDoBancoAsync(
        Func<CancellationToken, Task<IReadOnlyList<string>?>>? readPersonalDbWorkspacesAsync,
        CancellationToken ct)
    {
        if (readPersonalDbWorkspacesAsync is null)
        {
            return null;
        }

        try
        {
            return await readPersonalDbWorkspacesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem detalhe da exceção (ADR-013). "Não deu para ler" é resposta, e é a mais fraca.
            return null;
        }
    }

    /// <summary>
    /// A sonda online, com a MESMA recusa escrita nos dois pontos em que ela é consultada: falha de
    /// rede nunca vira resposta — nem "é de time", nem "é pessoal".
    /// </summary>
    private static async Task<bool> ProbeAsync(
        string serverWorkspaceId,
        Func<string, CancellationToken, Task<bool>> probeIsTeamAsync,
        CancellationToken ct)
    {
        try
        {
            return await probeIsTeamAsync(serverWorkspaceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SessionVaultScopeException(
                "Este computador ainda não conhece o cofre deste workspace. Conecte-se à internet uma "
                + "vez para o RemoteOps identificá-lo — enquanto isso, abra o seu cofre pessoal.",
                ex);
        }
    }

    private async Task<bool> HasTeamKeyAsync(string vaultWorkspaceId, CancellationToken ct)
    {
        using WorkspaceKey? wk = await _teamKeyRing
            .TryGetWorkspaceKeyAsync(vaultWorkspaceId, ct)
            .ConfigureAwait(false);
        return wk is not null;
    }

    /// <summary>
    /// Lê o marcador distinguindo AUSENTE de ILEGÍVEL — a distinção é o que impede "não consegui
    /// ler" de virar "primeira ativação" e regravar o dono com o workspace da vez. (A versão
    /// anterior colapsava os dois em <c>null</c>: "o pior que isso faz é uma regravação" — só que a
    /// regravação podia ser com o GUID do TIME, e aí o banco pessoal mudava de dono em silêncio.)
    /// </summary>
    /// <returns>
    /// <c>Legivel=true, Dono=null</c> — não há dono registrado (primeira ativação de verdade).
    /// <c>Legivel=true, Dono=x</c> — o dono é <c>x</c>.
    /// <c>Legivel=false</c> — o arquivo EXISTE e não deu para ler: quem chama recusa alto.
    /// </returns>
    private (bool Legivel, string? Dono) ReadOwnerMarker()
    {
        try
        {
            if (!File.Exists(_ownerMarkerPath))
            {
                return (true, null);
            }

            string conteudo = File.ReadAllText(_ownerMarkerPath).Trim();
            return (true, string.IsNullOrWhiteSpace(conteudo) ? null : conteudo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sumiu entre o Exists e a leitura? Então está mesmo ausente. Persistindo no disco e
            // ilegível, a resposta é "não sei" — e "não sei" recusa, nunca afirma.
            return (!File.Exists(_ownerMarkerPath), null);
        }
    }

    /// <summary>
    /// Escrita best-effort pelo MESMO motivo: falhar aqui não perde nada (a próxima abertura
    /// regrava), enquanto derrubar o boot por causa dela perderia o app inteiro.
    /// </summary>
    private void WriteOwnerMarker(string serverWorkspaceId)
    {
        try
        {
            File.WriteAllText(_ownerMarkerPath, serverWorkspaceId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Silêncio deliberado e limitado: o efeito é uma regravação no próximo boot.
        }
    }
}
