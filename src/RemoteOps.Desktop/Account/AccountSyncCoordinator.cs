using RemoteOps.Sync.Remote;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// Amarra o login E2EE ao resto do app (spec §8, plano T6). É o único lugar que conhece a ORDEM do
/// pós-login:
///
/// <list type="number">
///   <item>troca a raiz do cofre pra AMK + migra o cofre local (<see cref="IAccountVaultActivator"/>);</item>
///   <item>persiste os tokens no cofre já ativado (<c>VaultTokenStore</c>);</item>
///   <item>cacheia a AMK sob DPAPI (spec §4.3) — é o que dispensa a senha no próximo boot;</item>
///   <item>liga o sync — <see cref="StartSyncAsync"/>, best-effort.</item>
/// </list>
///
/// <para><b>A ordem não é arbitrária.</b> A raiz vem antes dos tokens porque o VaultTokenStore
/// escreve NO cofre: gravar sob a raiz DPAPI velha deixaria um envelope que o próximo boot (já sob
/// a AMK) não abriria, e o sync pediria relogin sem motivo. E o cache vem DEPOIS de tudo que pode
/// falhar: um cache gravado numa ativação incompleta faria o próximo boot pular o login e
/// reencontrar o mesmo cofre quebrado, sem caminho de recuperação.</para>
///
/// <para><b>Por que o sync é um passo SEPARADO</b> e não o fim da ativação: ele precisa do
/// <c>WorkspaceContext</c> (SQLCipher), que só nasce DEPOIS do cofre — que é o que a ativação
/// produz. A cadeia real é AMK → cofre → banco → sync, então o App ativa, monta o banco e só então
/// chama <see cref="StartSyncAsync"/>. Fingir que cabia tudo numa chamada só exigiria abrir o banco
/// duas vezes.</para>
///
/// <para><b>Postura de falha</b> (ADR-002, offline-first): cofre é obrigatório, sync é best-effort.
/// Se a migração falhar, estoura — seguir abriria o app com credenciais que não decifram. Se o sync
/// falhar, <see cref="StartSyncAsync"/> devolve <c>false</c> e o app abre do mesmo jeito: servidor
/// fora nunca impede o operador de trabalhar local.</para>
/// </summary>
public sealed class AccountSyncCoordinator
{
    private readonly IAmkCache _amkCache;
    private readonly IAccountVaultActivator _activator;
    private readonly IAccountSyncStarter _syncStarter;
    private readonly IReadOnlyList<string> _vaultWorkspaceIds;

    private ITokenStore? _tokens;
    private string? _activeWorkspaceId;

    /// <summary>
    /// Token store da conta ATIVA (ou null se não há sessão). Exposto pra o App montar um cliente
    /// autenticado pontual (ex.: gestão de 2FA nas Configurações) reusando o MESMO cache de tokens do
    /// sync — assim o refresh rotacionado num lugar não derruba o outro.
    /// </summary>
    public ITokenStore? ActiveTokenStore => _tokens;

    /// <param name="vaultWorkspaceIds">
    /// Workspaces do COFRE LOCAL (ex.: <c>ws-local</c>, <c>local</c>) — as identidades sob as quais
    /// os segredos deste device estão selados. Não confundir com o workspace do SERVIDOR (GUID), que
    /// vem da sessão. Ver <c>AppRuntime.VaultWorkspaces</c> pra por que são mais de um.
    /// </param>
    public AccountSyncCoordinator(
        IAmkCache amkCache,
        IAccountVaultActivator activator,
        IAccountSyncStarter syncStarter,
        IReadOnlyList<string> vaultWorkspaceIds)
    {
        ArgumentNullException.ThrowIfNull(amkCache);
        ArgumentNullException.ThrowIfNull(activator);
        ArgumentNullException.ThrowIfNull(syncStarter);
        ArgumentNullException.ThrowIfNull(vaultWorkspaceIds);
        if (vaultWorkspaceIds.Count == 0)
        {
            throw new ArgumentException(
                "Informe ao menos um workspace do cofre local a migrar.", nameof(vaultWorkspaceIds));
        }

        _amkCache = amkCache;
        _activator = activator;
        _syncStarter = syncStarter;
        _vaultWorkspaceIds = vaultWorkspaceIds;
    }

    /// <summary>
    /// Pós-login/registro (spec §6). CONSOME a <paramref name="session"/>: a AMK dela é zerada na
    /// saída — a partir daqui quem guarda a raiz é o cofre e o cache.
    /// </summary>
    public async Task<AccountActivation> ActivateFromLoginAsync(
        AccountSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            AccountActivation activation = await ActivateAsync(
                session.Email, session.WorkspaceId, session.Amk, session.Tokens, ct).ConfigureAwait(false);

            // Só agora o cache: se qualquer passo acima falhar, o próximo boot tem que pedir login
            // de novo em vez de reencontrar um estado meio-ativado.
            using (var entry = new CachedAccount(
                session.Email, session.WorkspaceId, AmkKeyVersionV1, session.Amk.ToArray()))
            {
                await _amkCache.SaveAsync(entry, ct).ConfigureAwait(false);
            }

            return activation;
        }
        finally
        {
            // A sessão veio da UI e morre aqui: a AMK dela não pode ficar viva na heap esperando o
            // GC. O cofre e o cache já têm as cópias deles.
            session.ZeroAmk();
        }
    }

    /// <summary>
    /// Relaunch (spec §6 "Unlock"): se há cache, abre o cofre SEM pedir senha e sem falar com o
    /// servidor. <c>null</c> = não há conta neste device → o App abre a janela de login.
    /// </summary>
    public async Task<AccountActivation?> TryActivateFromCacheAsync(CancellationToken ct = default)
    {
        using CachedAccount? cached = await _amkCache.LoadAsync(ct).ConfigureAwait(false);
        if (cached is null)
        {
            return null;
        }

        // tokens: null — o relaunch reusa os que já estão no cofre; só o login traz tokens novos.
        return await ActivateAsync(cached.Email, cached.WorkspaceId, cached.Amk, tokens: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Logout / trocar conta: apaga o cache da AMK e os tokens. Idempotente — e funciona mesmo sem
    /// sessão ativa nesta execução (o caso "abriu o app e quer trocar de conta"), porque o cache
    /// mora no disco, não na memória.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        // Cache primeiro: é ele que decide se o próximo boot pede senha. Se o processo morrer entre
        // as duas linhas, sobra um token órfão (inútil sem a AMK, e o servidor expira o refresh) —
        // o inverso deixaria o app entrando numa conta de que o operador já saiu.
        await _amkCache.ClearAsync(ct).ConfigureAwait(false);

        if (_tokens is not null)
        {
            await _tokens.ClearAsync(ct).ConfigureAwait(false);
            _tokens = null;
        }

        _activeWorkspaceId = null;
    }

    /// <summary>Versão do esquema de embrulho da AMK (spec §4.2 <c>amk_key_version</c>).</summary>
    private const int AmkKeyVersionV1 = 1;

    /// <summary>
    /// Liga o sync do workspace ativo. Chamado pelo App DEPOIS de montar o banco sobre o cofre já
    /// ativado. <c>false</c> = não subiu (servidor fora, rede caída, sem conta ativa) — e isso é
    /// rotina de campo, não erro: o app abre e trabalha local (ADR-002).
    /// </summary>
    public async Task<bool> StartSyncAsync(CancellationToken ct = default)
    {
        if (_activeWorkspaceId is null)
        {
            return false; // sem conta ativa não há o que sincronizar.
        }

        try
        {
            await _syncStarter.StartAsync(_activeWorkspaceId, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // shutdown do app: não é falha de sync, é o operador fechando.
        }
        catch (Exception)
        {
            // Sem detalhe no estado/log: no-secret-in-log (ADR-013). O status do sync na UI já
            // mostra "Offline"/"Erro de sincronização" pro operador.
            return false;
        }
    }

    /// <summary>O miolo comum ao login e ao cache: raiz + migração → tokens.</summary>
    /// <param name="tokens">Tokens novos (login), ou <c>null</c> pra reusar os do cofre (relaunch).</param>
    private async Task<AccountActivation> ActivateAsync(
        string email, string workspaceId, byte[] amk, TokenSet? tokens, CancellationToken ct)
    {
        // 1+2. Raiz do cofre → AMK e migração do cofre local. Obrigatório: se falhar, estoura.
        _tokens = await _activator.ActivateAsync(amk, workspaceId, _vaultWorkspaceIds, ct)
            .ConfigureAwait(false);

        // 3. Tokens no cofre JÁ ativado (a raiz acabou de virar AMK, então o envelope nasce sob ela).
        if (tokens is not null)
        {
            await _tokens.SaveAsync(tokens, ct).ConfigureAwait(false);
        }

        _activeWorkspaceId = workspaceId;
        return new AccountActivation(email, workspaceId);
    }
}
