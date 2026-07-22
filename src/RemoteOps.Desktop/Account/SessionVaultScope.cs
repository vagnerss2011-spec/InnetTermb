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
/// <para><b>Dois fatos persistidos, nenhum formato novo:</b> o marcador de raiz
/// (<c>KeyRooting["time:{W}"] == WkRandom</c>), gravado pela própria raiz do time sempre que a WK
/// aterrissa; e o arquivo <c>sync-local.owner</c>, que diz qual workspace de servidor é DONO do
/// banco pessoal desta máquina. O segundo é um arquivo NOVO, e não um rename de
/// <c>sync-local.db</c>, de propósito: um rename meio-feito (banco movido, <c>.keyref</c> para trás)
/// deixaria o operador sem os ~700 e sem caminho de volta; um marcador que falha ao ser escrito não
/// perde nada.</para>
/// </summary>
internal sealed class SessionVaultScopeResolver
{
    private readonly WkWorkspaceKeyRing _teamKeyRing;
    private readonly IVaultRootingStore _rooting;
    private readonly string _ownerMarkerPath;
    private readonly string _personalDbPath;

    /// <param name="personalDbPath">
    /// O caminho do banco pessoal (<c>sync-local.db</c>). A EXISTÊNCIA dele é o que separa "máquina
    /// que já usava o RemoteOps antes dos times" (adota o dono sem rede — o caso de toda a frota no
    /// upgrade) de "máquina nova" (a sonda decide, porque o primeiro workspace aberto aqui pode
    /// muito bem ser o do TIME).
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
    /// <exception cref="SessionVaultScopeException">
    /// Não deu para decidir com honestidade. Recusar é obrigatório: assumir "pessoal" abriria
    /// <c>sync-local.db</c> e <c>ws-local</c> sincronizando contra um workspace de servidor que não é
    /// dono deles — os hosts pessoais do operador subiriam para o workspace de outra pessoa.
    /// </exception>
    internal async Task<SessionVaultScope> ResolveAsync(
        string serverWorkspaceId,
        Func<string, CancellationToken, Task<bool>> probeIsTeamAsync,
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
            // O banco pessoal já existe → esta máquina usava o RemoteOps antes dos times (o caso de
            // TODA a frota no upgrade): `sync-local.db` é o banco pessoal deste workspace, por
            // construção — não existia como criar workspace de time. Adota o dono, sem rede.
            if (File.Exists(_personalDbPath))
            {
                WriteOwnerMarker(serverWorkspaceId);
                return SessionVaultScope.Personal;
            }

            // Máquina NOVA. O primeiro workspace aberto aqui pode muito bem ser o do TIME (o
            // segundo PC do colega, escolhendo o time no chooser) — adotá-lo às cegas amarraria o
            // banco pessoal ao workspace dos colegas. A sonda decide; e a primeira abertura de uma
            // máquina nova acabou de exigir a rede para o próprio login, então perguntar aqui não
            // tranca ninguém que já conseguia trabalhar.
            if (await ProbeAsync(serverWorkspaceId, probeIsTeamAsync, ct).ConfigureAwait(false))
            {
                // A sonda restaura a chave na mesma ida; se não restaurou, fail-closed e alto.
                return SessionVaultScope.Team(
                    serverWorkspaceId,
                    temChave: await HasTeamKeyAsync(vaultWorkspaceId, ct).ConfigureAwait(false));
            }

            // "Não é de time" — é o cofre pessoal desta conta: adota como dono do banco pessoal.
            WriteOwnerMarker(serverWorkspaceId);
            return SessionVaultScope.Personal;
        }

        // REGRA 6 — há um dono registrado (outro workspace) e nenhuma evidência local: workspace
        // que esta máquina nunca viu. UM round-trip, uma vez na vida deste par (máquina,
        // workspace): a sonda restaura a chave na mesma ida, e a regra 3 responde daqui em diante.
        if (!await ProbeAsync(serverWorkspaceId, probeIsTeamAsync, ct).ConfigureAwait(false))
        {
            // O servidor respondeu: não há chave de time aqui. Como este workspace TAMBÉM não é o
            // dono do banco pessoal desta máquina, ele é o cofre pessoal de outra conta/instalação.
            throw new SessionVaultScopeException(
                "Este workspace é o cofre pessoal de outra instalação do RemoteOps, e abri-lo aqui "
                + "misturaria os dois acervos. Abra o seu cofre pessoal, ou peça um convite para "
                + "entrar num time.");
        }

        // A sonda restaurou a chave? Se sim, cofre do time completo. Se não, fail-closed e alto —
        // "o servidor disse que é de time" não é licença para escrever sem chave.
        return SessionVaultScope.Team(
            serverWorkspaceId,
            temChave: await HasTeamKeyAsync(vaultWorkspaceId, ct).ConfigureAwait(false));
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
