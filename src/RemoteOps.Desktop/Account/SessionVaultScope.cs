using System.IO;

using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.Desktop.Account;

/// <summary>Que cofre esta sessão do app abriu.</summary>
internal enum SessionVaultKind
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

    internal SessionVaultScopeResolver(
        WkWorkspaceKeyRing teamKeyRing, IVaultRootingStore rooting, string ownerMarkerPath)
    {
        ArgumentNullException.ThrowIfNull(teamKeyRing);
        ArgumentNullException.ThrowIfNull(rooting);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerMarkerPath);

        _teamKeyRing = teamKeyRing;
        _rooting = rooting;
        _ownerMarkerPath = ownerMarkerPath;
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

        string? dono = ReadOwnerMarker();

        // REGRA 1 — primeira ativação depois do upgrade. Hoje `sync-local.db` É o banco pessoal
        // deste workspace, por construção: não existia como criar workspace de time antes do 1g.
        if (dono is null)
        {
            WriteOwnerMarker(serverWorkspaceId);
            return SessionVaultScope.Personal;
        }

        // REGRA 2 — o caso do operador, sempre. E ela vem ANTES de qualquer regra de time: é isto
        // que impede o app dele de mudar de cofre sozinho por causa do blob `wk:time:{W_pessoal}`
        // que o build 1e gravou no instante em que ele clicou "convidar".
        if (string.Equals(dono, serverWorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            return SessionVaultScope.Personal;
        }

        string vaultWorkspaceId = AppRuntime.TeamVaultWorkspace(serverWorkspaceId);

        // REGRA 3 — a WK está aqui: é cofre de time, e a resposta vale OFFLINE (a condição normal
        // de campo, e o caminho de todo boot depois do primeiro).
        if (await HasTeamKeyAsync(vaultWorkspaceId, ct).ConfigureAwait(false))
        {
            return SessionVaultScope.Team(serverWorkspaceId, temChave: true);
        }

        // REGRA 4 — a chave já aterrissou aqui um dia (marcador), mas o blob não está mais. Cair em
        // "pessoal" aqui é o desastre; fail-closed é a resposta.
        if (await _rooting.LoadKeyRootingAsync(vaultWorkspaceId, ct).ConfigureAwait(false)
            is VaultKeyRooting.WkRandom)
        {
            return SessionVaultScope.Team(serverWorkspaceId, temChave: false);
        }

        // REGRA 5 — workspace que esta máquina nunca viu. UM round-trip, uma vez na vida deste
        // par (máquina, workspace): a sonda restaura a chave na mesma ida, e a regra 3 responde
        // daqui em diante.
        bool ehDeTime;
        try
        {
            ehDeTime = await probeIsTeamAsync(serverWorkspaceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SessionVaultScopeException(
                "Este computador ainda não conhece o cofre deste workspace. Conecte-se à internet uma "
                + "vez para o RemoteOps identificá-lo — enquanto isso, abra o seu cofre pessoal.",
                ex);
        }

        if (!ehDeTime)
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

    private async Task<bool> HasTeamKeyAsync(string vaultWorkspaceId, CancellationToken ct)
    {
        using WorkspaceKey? wk = await _teamKeyRing
            .TryGetWorkspaceKeyAsync(vaultWorkspaceId, ct)
            .ConfigureAwait(false);
        return wk is not null;
    }

    /// <summary>
    /// Leitura fail-safe: arquivo ilegível é tratado como "não sei" (marcador ausente), e não como
    /// erro de boot. O pior que isso faz é uma regravação; travar o app por causa de um arquivo de
    /// 36 bytes seria trocar um problema pequeno por um que ninguém consegue destravar.
    /// </summary>
    private string? ReadOwnerMarker()
    {
        try
        {
            if (!File.Exists(_ownerMarkerPath))
            {
                return null;
            }

            string conteudo = File.ReadAllText(_ownerMarkerPath).Trim();
            return string.IsNullOrWhiteSpace(conteudo) ? null : conteudo;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
