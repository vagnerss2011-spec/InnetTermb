using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// O wiring da Fase 1 (T6): pós-login → tokens no cofre → AMK cacheada (DPAPI) → raiz do cofre
/// trocada pra AMK → migração local → sync ligado. E o inverso no logout.
///
/// Tudo com fakes: nada de rede, disco ou DPAPI. O que está sob teste é a ORDEM e as garantias —
/// principalmente as duas que quebram o operador se saírem erradas: (1) o app abre mesmo com o
/// servidor fora, (2) o logout não deixa material de chave pra trás.
/// </summary>
public sealed class AccountSyncCoordinatorTests
{
    private const string Email = "op@innet.tec.br";
    private const string WorkspaceId = "ws-guid";
    private const string VaultWorkspace = "ws-local";

    private static byte[] SampleAmk() => [.. Enumerable.Range(0, 32).Select(i => (byte)(i + 1))];

    private static AccountSession NewSession(byte[]? amk = null) => new(
        Email,
        WorkspaceId,
        amk ?? SampleAmk(),
        new TokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1)),
        [new AccountWorkspace(WorkspaceId, "NOC", "Owner")]);

    // ── Fakes ────────────────────────────────────────────────────────────────────────────

    private sealed class FakeAmkCache : IAmkCache
    {
        private CachedAccount? _entry;

        public int SaveCount { get; private set; }
        public int ClearCount { get; private set; }
        public CachedAccount? Entry => _entry;

        public void Seed(CachedAccount entry) => _entry = entry;

        public Task<CachedAccount?> LoadAsync(CancellationToken ct = default)
        {
            // Cada Load devolve uma instância NOVA, como o cache real (que relê o disco): devolver
            // a mesma faria o Dispose de um chamador zerar a AMK do outro.
            return Task.FromResult(_entry is null
                ? null
                : new CachedAccount(_entry.Email, _entry.WorkspaceId, _entry.AmkKeyVersion, _entry.Amk.ToArray()));
        }

        public Task SaveAsync(CachedAccount account, CancellationToken ct = default)
        {
            _entry = new CachedAccount(
                account.Email, account.WorkspaceId, account.AmkKeyVersion, account.Amk.ToArray());
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _entry?.Dispose();
            _entry = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Ativador fake: registra a AMK com que a raiz foi trocada e o que foi migrado.</summary>
    private sealed class FakeVaultActivator : IAccountVaultActivator
    {
        public List<byte[]> ActivatedWith { get; } = [];
        public List<string> Migrated { get; } = [];
        public FakeTokenStore Tokens { get; } = new();
        public Exception? Throw { get; set; }

        public Task<ITokenStore> ActivateAsync(
            ReadOnlyMemory<byte> amk, string vaultWorkspaceId, CancellationToken ct = default)
        {
            if (Throw is not null)
            {
                return Task.FromException<ITokenStore>(Throw);
            }

            // Cópia: o coordenador pode zerar a dele depois — o teste precisa do valor de então.
            ActivatedWith.Add(amk.ToArray());
            Migrated.Add(vaultWorkspaceId);
            return Task.FromResult<ITokenStore>(Tokens);
        }
    }

    private sealed class FakeTokenStore : ITokenStore
    {
        public TokenSet? Current { get; private set; }
        public int ClearCount { get; private set; }

        public Task<TokenSet?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);

        public Task SaveAsync(TokenSet tokens, CancellationToken ct = default)
        {
            Current = tokens;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Current = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSyncStarter : IAccountSyncStarter
    {
        public List<string> Started { get; } = [];
        public Exception? Throw { get; set; }

        public Task StartAsync(string workspaceId, CancellationToken ct = default)
        {
            Started.Add(workspaceId);
            return Throw is not null ? Task.FromException(Throw) : Task.CompletedTask;
        }
    }

    private static AccountSyncCoordinator NewCoordinator(
        FakeAmkCache cache, FakeVaultActivator activator, FakeSyncStarter sync)
        => new(cache, activator, sync, VaultWorkspace);

    // ── (a) pós-login: tokens + cache + migração + sync ───────────────────────────────────

    /// <summary>
    /// O fluxo completo da spec §6 depois de um login bem-sucedido. Se qualquer um dos quatro
    /// passos sumir, o operador perde algo concreto: os tokens (relogin toda vez), o cache (senha
    /// toda vez), a migração (cofre local ilegível sob a raiz nova) ou o sync (nada sobe).
    /// </summary>
    [Fact]
    public async Task ActivateFromLogin_PersistsTokens_CachesAmk_Migrates_AndStartsSync()
    {
        var cache = new FakeAmkCache();
        var activator = new FakeVaultActivator();
        var sync = new FakeSyncStarter();
        AccountSession session = NewSession();

        await NewCoordinator(cache, activator, sync).ActivateFromLoginAsync(session);

        // a. tokens persistidos no cofre (VaultTokenStore em produção)
        Assert.Equal("access", activator.Tokens.Current!.AccessToken);
        Assert.Equal("refresh", activator.Tokens.Current.RefreshToken);

        // b. AMK cacheada sob a identidade certa
        Assert.Equal(1, cache.SaveCount);
        Assert.Equal(SampleAmk(), cache.Entry!.Amk);
        Assert.Equal(Email, cache.Entry.Email);
        Assert.Equal(WorkspaceId, cache.Entry.WorkspaceId);

        // c+d. raiz trocada pra AMK e cofre local migrado
        Assert.Equal(SampleAmk(), Assert.Single(activator.ActivatedWith));
        Assert.Equal(VaultWorkspace, Assert.Single(activator.Migrated));

        // e. sync ligado no workspace do servidor
        Assert.Equal(WorkspaceId, Assert.Single(sync.Started));
    }

    /// <summary>
    /// A raiz tem que ser trocada ANTES de os tokens serem gravados: o VaultTokenStore escreve NO
    /// cofre, e gravar sob a raiz DPAPI velha deixaria um envelope que o próximo boot (já sob a AMK)
    /// não abre — sync pedindo relogin sem motivo.
    /// </summary>
    [Fact]
    public async Task ActivateFromLogin_SwapsRootBeforeWritingTokens()
    {
        var cache = new FakeAmkCache();
        var sync = new FakeSyncStarter();
        var activator = new FakeVaultActivator();

        await NewCoordinator(cache, activator, sync).ActivateFromLoginAsync(NewSession());

        // O token store só EXISTE depois do ActivateAsync — é ele quem o devolve. Se os tokens
        // estão lá, a troca de raiz aconteceu antes por construção.
        Assert.NotEmpty(activator.ActivatedWith);
        Assert.NotNull(activator.Tokens.Current);
    }

    /// <summary>A sessão é consumida: a AMK dela é zerada, e quem fica com a cópia viva é o cofre.</summary>
    [Fact]
    public async Task ActivateFromLogin_ZeroesTheSessionAmk()
    {
        var session = NewSession();
        byte[] sessionAmk = session.Amk;

        await NewCoordinator(new FakeAmkCache(), new FakeVaultActivator(), new FakeSyncStarter())
            .ActivateFromLoginAsync(session);

        Assert.True(sessionAmk.All(b => b == 0));
    }

    // ── (b) relaunch com cache: abre sem senha ───────────────────────────────────────────

    /// <summary>
    /// O ponto da spec §4.3: reabrir o app com o cache presente NÃO pede senha — nem toca no
    /// servidor. Devolve a conta e liga o sync.
    /// </summary>
    [Fact]
    public async Task TryActivateFromCache_WithCache_OpensWithoutPassword()
    {
        var cache = new FakeAmkCache();
        cache.Seed(new CachedAccount(Email, WorkspaceId, 1, SampleAmk()));
        var activator = new FakeVaultActivator();
        var sync = new FakeSyncStarter();

        AccountActivation? result = await NewCoordinator(cache, activator, sync).TryActivateFromCacheAsync();

        Assert.NotNull(result);
        Assert.Equal(Email, result!.Email);
        Assert.Equal(WorkspaceId, result.WorkspaceId);
        Assert.Equal(SampleAmk(), Assert.Single(activator.ActivatedWith));
        Assert.Equal(VaultWorkspace, Assert.Single(activator.Migrated));
        Assert.Equal(WorkspaceId, Assert.Single(sync.Started));
        // Nada de reescrever o cache num caminho que só leu.
        Assert.Equal(0, cache.SaveCount);
    }

    // ── (c) cache ausente: pede login ────────────────────────────────────────────────────

    /// <summary>Sem cache não há conta: o coordenador diz "null" e o App abre a AccountWindow.</summary>
    [Fact]
    public async Task TryActivateFromCache_WithoutCache_ReturnsNull()
    {
        var activator = new FakeVaultActivator();
        var sync = new FakeSyncStarter();

        AccountActivation? result = await NewCoordinator(new FakeAmkCache(), activator, sync)
            .TryActivateFromCacheAsync();

        Assert.Null(result);
        // Sem AMK, nada pode ser tocado: não troca raiz, não migra, não liga sync.
        Assert.Empty(activator.ActivatedWith);
        Assert.Empty(sync.Started);
    }

    // ── (d) servidor fora: o app abre offline ────────────────────────────────────────────

    /// <summary>
    /// A garantia offline-first (ADR-002): servidor fora NÃO bloqueia. O cache abre o cofre local,
    /// a migração roda, e só o sync degrada — o app abre e funciona.
    /// </summary>
    [Fact]
    public async Task TryActivateFromCache_WhenServerIsDown_StillOpensTheVault()
    {
        var cache = new FakeAmkCache();
        cache.Seed(new CachedAccount(Email, WorkspaceId, 1, SampleAmk()));
        var activator = new FakeVaultActivator();
        var sync = new FakeSyncStarter { Throw = new HttpRequestException("servidor fora") };

        AccountActivation? result = await NewCoordinator(cache, activator, sync).TryActivateFromCacheAsync();

        // A conta abriu, a raiz foi trocada e o cofre migrou — apesar do sync ter falhado.
        Assert.NotNull(result);
        Assert.Equal(SampleAmk(), Assert.Single(activator.ActivatedWith));
        Assert.Equal(VaultWorkspace, Assert.Single(activator.Migrated));
    }

    /// <summary>Mesma postura no pós-login: o sync falhar não desfaz um login que deu certo.</summary>
    [Fact]
    public async Task ActivateFromLogin_WhenSyncFails_StillActivatesTheAccount()
    {
        var cache = new FakeAmkCache();
        var activator = new FakeVaultActivator();
        var sync = new FakeSyncStarter { Throw = new HttpRequestException("servidor fora") };

        AccountActivation result = await NewCoordinator(cache, activator, sync)
            .ActivateFromLoginAsync(NewSession());

        Assert.Equal(Email, result.Email);
        Assert.Equal(1, cache.SaveCount);
        Assert.NotNull(activator.Tokens.Current);
    }

    /// <summary>
    /// A migração NÃO é best-effort: se ela falhar, a raiz do cofre está inconsistente e seguir
    /// abriria o app com credenciais que não decifram. Estoura pra o App tratar (e cair no modo
    /// local com a raiz antiga) em vez de fingir que deu certo.
    /// </summary>
    [Fact]
    public async Task ActivateFromLogin_WhenMigrationFails_Throws()
    {
        var cache = new FakeAmkCache();
        var activator = new FakeVaultActivator { Throw = new InvalidOperationException("cofre travado") };
        var sync = new FakeSyncStarter();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewCoordinator(cache, activator, sync).ActivateFromLoginAsync(NewSession()));

        // Falhou antes de cachear: um cache de uma ativação que não completou faria o próximo boot
        // pular o login e reencontrar exatamente o mesmo cofre quebrado, sem chance de se recuperar.
        Assert.Equal(0, cache.SaveCount);
        Assert.Empty(sync.Started);
    }

    // ── (e) logout: zera cache e tokens ──────────────────────────────────────────────────

    /// <summary>
    /// Logout/trocar conta: o cache da AMK e os tokens somem. Se algum ficar, o próximo boot entra
    /// na conta que o operador acabou de sair.
    /// </summary>
    [Fact]
    public async Task Logout_ClearsAmkCacheAndTokens()
    {
        var cache = new FakeAmkCache();
        var activator = new FakeVaultActivator();
        var sync = new FakeSyncStarter();
        AccountSyncCoordinator coordinator = NewCoordinator(cache, activator, sync);
        await coordinator.ActivateFromLoginAsync(NewSession());

        await coordinator.LogoutAsync();

        Assert.Equal(1, cache.ClearCount);
        Assert.Null(cache.Entry);
        Assert.Null(activator.Tokens.Current);
        Assert.Equal(1, activator.Tokens.ClearCount);
        Assert.Null(await NewCoordinator(cache, activator, sync).TryActivateFromCacheAsync());
    }

    /// <summary>
    /// Logout sem sessão ativa (o app nunca logou nesta execução) ainda tem que limpar o cache do
    /// disco — é exatamente o caso de "trocar conta" logo depois de abrir o app.
    /// </summary>
    [Fact]
    public async Task Logout_WithoutActiveSession_StillClearsTheCache()
    {
        var cache = new FakeAmkCache();
        cache.Seed(new CachedAccount(Email, WorkspaceId, 1, SampleAmk()));

        await NewCoordinator(cache, new FakeVaultActivator(), new FakeSyncStarter()).LogoutAsync();

        Assert.Equal(1, cache.ClearCount);
        Assert.Null(cache.Entry);
    }

    /// <summary>Logout é idempotente: clicar duas vezes não pode explodir.</summary>
    [Fact]
    public async Task Logout_Twice_IsIdempotent()
    {
        var cache = new FakeAmkCache();
        var activator = new FakeVaultActivator();
        AccountSyncCoordinator coordinator = NewCoordinator(cache, activator, new FakeSyncStarter());
        await coordinator.ActivateFromLoginAsync(NewSession());

        await coordinator.LogoutAsync();
        await coordinator.LogoutAsync();

        Assert.Null(cache.Entry);
    }
}
